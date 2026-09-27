using System.Globalization;
using System.Runtime;
using System.Text;
using ChatterFix.Core;
using ChatterFix.Core.Diagnostics;
using ChatterFix.Core.Filtering;
using ChatterFix.Core.Native;

namespace ChatterFix.Cli;

/// <summary>
/// Diagnostic tool: measures mouse clicks live and shows which ones are faulty.
/// In its default mode it blocks nothing at all, it only listens.
/// </summary>
internal static class Program
{
    private static volatile bool _stopRequested;

    private static int Main(string[] args)
    {
        TryEnableUnicodeOutput();

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        // Garbage collector pauses would delay the hook callback.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        if (args.Contains("--selftest"))
            return RunSelfTest();

        bool protect = args.Contains("--protect");
        int threshold = ReadIntOption(args, "--threshold", 25);
        int releaseDelay = ReadIntOption(args, "--release-delay", 12);
        int seconds = ReadIntOption(args, "--seconds", 0);
        bool quiet = args.Contains("--quiet");
        string? reportPath = ReadStringOption(args, "--report");

        var settings = new FilterSettings
        {
            Mode = protect ? FilterMode.Protect : FilterMode.Monitor,
            Enabled = true,
            Buttons =
            [
                .. FilterSettings.CreateDefaultButtons()
                    .Select(b => b with { ChatterThresholdMs = threshold, ReleaseDelayMs = releaseDelay })
            ],
        };

        // Disposed after the hook, so any release still held back is sent on the way out.
        using var releaseGate = protect && releaseDelay > 0
            ? new ReleaseScheduler(
                MonotonicClock.NowMicroseconds,
                b => InputInjector.SendButton(b, MouseEventKind.Up),
                b => InputInjector.SendButton(b, MouseEventKind.Down))
            : null;

        var filter = new ClickFilter(settings, releaseGate: releaseGate);
        using var hook = new LowLevelMouseHook(filter);

        try
        {
            hook.Start();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Could not install the hook: " + ex.Message);
            Console.ResetColor();
            return 1;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _stopRequested = true;
        };

        var startedAt = DateTime.Now;
        var deadline = seconds > 0 ? startedAt.AddSeconds(seconds) : DateTime.MaxValue;

        if (quiet)
        {
            RunQuiet(filter, startedAt, deadline, seconds);
        }
        else
        {
            bool cursorHidden = TrySetCursorVisible(false);
            Console.Clear();

            while (!_stopRequested && DateTime.Now < deadline)
            {
                Render(filter, hook, settings, startedAt);
                Thread.Sleep(200);
            }

            if (cursorHidden) TrySetCursorVisible(true);
            Console.Clear();
        }

        PrintSummary(filter, startedAt);

        if (reportPath is not null)
        {
            try
            {
                MeasurementReport.Write(reportPath, filter, startedAt, DateTime.Now);
                Console.WriteLine();
                Console.WriteLine($"  Report written to {Path.GetFullPath(reportPath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Could not write the report: {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Proves the hook is live without touching anything on screen: it injects side-button
    /// events and suppresses them inside the callback, so they never reach any application.
    /// A pass means both halves work — events are seen, and suppression stops them.
    /// </summary>
    private static int RunSelfTest()
    {
        const int pairs = 3;

        var sink = new SelfTestSink();
        using var hook = new LowLevelMouseHook(sink);

        try
        {
            hook.Start();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAIL: could not install the hook: " + ex.Message);
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine("Self-test");
        Console.WriteLine("  Hook installed. Injecting synthetic side-button events...");

        for (int i = 0; i < pairs; i++)
        {
            InputInjector.SendButton(MouseButton.X2, MouseEventKind.Down);
            Thread.Sleep(20);
            InputInjector.SendButton(MouseButton.X2, MouseEventKind.Up);
            Thread.Sleep(30);
        }

        Thread.Sleep(250); // let the last events drain

        int captured = sink.Captured;
        const int expected = pairs * 2;

        Console.WriteLine($"  Events injected : {expected}");
        Console.WriteLine($"  Events captured : {captured}");
        Console.WriteLine($"  Events suppressed by the hook: {hook.SuppressedEvents}");
        Console.WriteLine();

        if (captured >= expected)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  PASS — the hook sees mouse events and can block them.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  FAIL — the hook did not receive the injected events.");
        Console.ResetColor();
        Console.WriteLine("  Another low-level hook may be blocking events ahead of this one,");
        Console.WriteLine("  or the process needs the same privilege level as the foreground app.");
        return 1;
    }

    /// <summary>Captures injected side-button events and stops them from reaching the system.</summary>
    private sealed class SelfTestSink : IMouseEventSink
    {
        private int _captured;

        public int Captured => Volatile.Read(ref _captured);

        public FilterResult Handle(in MouseEvent e)
        {
            if (e.Button == MouseButton.X2 && e.IsInjected)
            {
                Interlocked.Increment(ref _captured);
                return new FilterResult(FilterAction.Suppress, FilterReason.Injected, -1);
            }

            return new FilterResult(FilterAction.Pass, FilterReason.Normal, -1);
        }
    }

    /// <summary>Measures without drawing the live table, for when the output is read by another tool.</summary>
    private static void RunQuiet(ClickFilter filter, DateTime startedAt, DateTime deadline, int seconds)
    {
        Console.WriteLine(seconds > 0
            ? $"  Measuring for {seconds} seconds — use your mouse normally."
            : "  Measuring — press Ctrl+C to stop.");
        Console.WriteLine("  Click normally, jitter click, and hold a button down for a while.");
        Console.WriteLine();

        int lastReported = -1;
        while (!_stopRequested && DateTime.Now < deadline)
        {
            Thread.Sleep(250);

            int elapsed = (int)(DateTime.Now - startedAt).TotalSeconds;
            if (elapsed != lastReported && elapsed % 5 == 0)
            {
                lastReported = elapsed;
                long downs = filter.Statistics[MouseButton.Left].Downs;
                long suspicious = filter.Statistics[MouseButton.Left].ReleaseGap.CountBelow(25);
                Console.WriteLine($"    {elapsed,3}s — left presses: {downs,4}   suspicious (<25 ms): {suspicious}");
            }
        }
    }

    private static void Render(ClickFilter filter, LowLevelMouseHook hook, FilterSettings settings, DateTime startedAt)
    {
        var sb = new StringBuilder(4096);
        var stats = filter.Statistics;
        bool protect = settings.Mode == FilterMode.Protect;

        AppendLine(sb, "╔══════════════════════════════════════════════════════════════════════╗");
        AppendLine(sb, "║  ChatterFix — mouse double-click diagnostics                          ║");
        AppendLine(sb, "╚══════════════════════════════════════════════════════════════════════╝");
        AppendLine(sb, "");

        string mode = protect
            ? $"PROTECT (chatter threshold {settings.Buttons[0].ChatterThresholdMs} ms, "
              + $"drop repair {settings.Buttons[0].ReleaseDelayMs} ms)"
            : "MONITOR (nothing is blocked, clicks are only measured)";

        AppendLine(sb, $"  Mode     : {mode}");
        AppendLine(sb, $"  Elapsed  : {(DateTime.Now - startedAt):hh\\:mm\\:ss}   Events seen: {hook.SeenEvents}   Blocked: {hook.SuppressedEvents}");
        AppendLine(sb, "");

        AppendLine(sb, "  ┌────────┬─────────┬──────────┬───────────┬───────────┬──────────────┐");
        AppendLine(sb, "  │ Button │ Presses │ Shortest │  Average  │ Suspicious│  Fault rate  │");
        AppendLine(sb, "  │        │         │   gap    │    gap    │  (<25 ms) │              │");
        AppendLine(sb, "  ├────────┼─────────┼──────────┼───────────┼───────────┼──────────────┤");

        for (int i = 0; i < ClickStatistics.ButtonCount; i++)
        {
            var button = (MouseButton)i;
            var s = stats[button];
            if (s.Downs == 0) continue;

            long suspicious = s.ReleaseGap.CountBelow(25) + s.ChatterSuppressed;
            double rate = 100.0 * suspicious / s.Downs;

            AppendLine(sb, string.Format(CultureInfo.InvariantCulture,
                "  │ {0,-6} │ {1,7} │ {2,7:F1}ms │ {3,8:F1}ms │ {4,9} │ {5,11:F1}% │",
                ClickStatistics.ButtonName(button),
                s.Downs,
                s.ReleaseGap.MinMs,
                s.ReleaseGap.AverageMs,
                suspicious,
                rate));
        }

        AppendLine(sb, "  └────────┴─────────┴──────────┴───────────┴───────────┴──────────────┘");
        AppendLine(sb, "");

        AppendHistogram(sb, stats[MouseButton.Left]);
        AppendLine(sb, "");
        AppendRecentEvents(sb, filter.Events);
        AppendLine(sb, "");
        AppendLine(sb, "  Press Ctrl+C to stop. Keep using your mouse normally — measuring in the background.");

        Console.SetCursorPosition(0, 0);
        Console.Write(sb.ToString());
    }

    private static void AppendHistogram(StringBuilder sb, ButtonStatistics stats)
    {
        AppendLine(sb, $"  Left button — gap between a release and the next press  (n={stats.ReleaseGap.Total})");

        var counts = stats.ReleaseGap.Snapshot();
        long max = counts.Length == 0 ? 0 : counts.Max();
        if (max == 0)
        {
            AppendLine(sb, "    (no data yet — click a few times)");
            return;
        }

        for (int i = 0; i < IntervalHistogram.UpperBoundsMs.Length; i++)
        {
            if (counts[i] == 0) continue;

            int lower = i == 0 ? 0 : IntervalHistogram.UpperBoundsMs[i - 1];
            int upper = IntervalHistogram.UpperBoundsMs[i];
            string range = upper == int.MaxValue ? $"{lower}+ ms" : $"{lower}-{upper} ms";

            int barLength = (int)(counts[i] * 34 / max);
            string bar = new('█', Math.Max(barLength, 1));
            string flag = upper <= 25 ? "  <- FAULT" : "";

            AppendLine(sb, $"    {range,-12} {bar,-34} {counts[i],5}{flag}");
        }
    }

    private static void AppendRecentEvents(StringBuilder sb, EventRing ring)
    {
        AppendLine(sb, "  Recent events:");
        var recent = ring.TakeLatest(6);
        if (recent.Length == 0)
        {
            AppendLine(sb, "    (nothing yet)");
            return;
        }

        foreach (var e in recent)
        {
            string kind = e.Kind == MouseEventKind.Down ? "press  " : "release";
            string gap = e.GapMs < 0 ? "    -   " : $"{e.GapMs,7:F1}ms";
            string verdict = e.Reason switch
            {
                FilterReason.ChatterDown => "FAULT - blocked",
                FilterReason.OrphanUp => "matching release - blocked",
                FilterReason.ReleaseRepaired => "dropped connection - repaired",
                FilterReason.ReleaseHeld => "release held back",
                FilterReason.Injected => "from software - untouched",
                _ => "normal",
            };

            AppendLine(sb, string.Format(CultureInfo.InvariantCulture,
                "    {0,8:F2}s  {1,-6} {2,-7} {3}  {4}",
                e.TimestampSeconds, ClickStatistics.ButtonName(e.Button), kind, gap, verdict));
        }
    }

    private static void PrintSummary(ClickFilter filter, DateTime startedAt)
    {
        var stats = filter.Statistics;

        Console.WriteLine();
        Console.WriteLine("========================  MEASUREMENT SUMMARY  ========================");
        Console.WriteLine($"  Duration: {(DateTime.Now - startedAt):hh\\:mm\\:ss}");
        Console.WriteLine();

        bool anyData = false;

        for (int i = 0; i < ClickStatistics.ButtonCount; i++)
        {
            var button = (MouseButton)i;
            var s = stats[button];
            if (s.Downs == 0) continue;

            anyData = true;
            Console.WriteLine($"  {ClickStatistics.ButtonName(button)} button: {s.Downs} presses, shortest gap {s.ReleaseGap.MinMs:F1} ms");

            long below10 = s.ReleaseGap.CountBelow(10);
            long below25 = s.ReleaseGap.CountBelow(25);

            if (below25 == 0)
            {
                Console.WriteLine("    No sign of chatter. This button looks healthy.");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    Under 25 ms: {below25} events (under 10 ms: {below10}) -> chatter detected");
                Console.ResetColor();
            }

            int gap = s.ReleaseGap.FindFirstGapUpperBoundMs();
            if (gap > 0 && below25 > 0)
                Console.WriteLine($"    Suggested threshold: {gap} ms (the empty band between faults and real clicks)");

            Console.WriteLine();
        }

        if (!anyData)
        {
            Console.WriteLine("  No clicks were recorded.");
            return;
        }

        if (filter.Settings.Mode == FilterMode.Protect)
        {
            Console.WriteLine($"  Faulty clicks blocked in this session: {stats.TotalChatterSuppressed}");
            Console.WriteLine($"  Dropped connections repaired:          {stats.TotalReleaseRepairs}");
        }
        else
            Console.WriteLine("  Protection was off (monitor mode). Turn it on with: chatterfix-diag --protect");
    }

    private static void AppendLine(StringBuilder sb, string text)
    {
        // Pad to the console width so nothing is left over from the previous frame.
        int width = SafeWindowWidth();
        if (text.Length < width - 1) text = text.PadRight(width - 1);
        sb.Append(text).Append('\n');
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Math.Clamp(Console.WindowWidth, 60, 200);
        }
        catch
        {
            return 80;
        }
    }

    private static int ReadIntOption(string[] args, string name, int fallback)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) return fallback;
        return int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static string? ReadStringOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) return null;
        return args[index + 1];
    }

    private static void TryEnableUnicodeOutput()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
        }
    }

    private static bool TrySetCursorVisible(bool visible)
    {
        try
        {
            Console.CursorVisible = visible;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("ChatterFix diagnostics");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  chatterfix-diag                   Monitor mode - nothing is blocked");
        Console.WriteLine("  chatterfix-diag --protect         Protect mode - faulty clicks are blocked");
        Console.WriteLine("  chatterfix-diag --threshold 25    Chatter threshold in ms (default 25)");
        Console.WriteLine("  chatterfix-diag --release-delay 12  Drop repair window in ms, 0 disables it");
        Console.WriteLine("  chatterfix-diag --seconds 60      Measure for 60 seconds, then exit");
        Console.WriteLine("  chatterfix-diag --quiet           Skip the live table, print progress only");
        Console.WriteLine("  chatterfix-diag --report r.json   Save the measurement as JSON");
        Console.WriteLine("  chatterfix-diag --selftest        Verify the hook works, then exit");
    }
}
