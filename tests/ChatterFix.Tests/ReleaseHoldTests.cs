using ChatterFix.Core;
using ChatterFix.Core.Filtering;

namespace ChatterFix.Tests;

/// <summary>
/// Covers the drop repair: a release held back briefly, then either cancelled
/// because the button was never really let go, or sent on when it was.
/// </summary>
public class ReleaseHoldTests
{
    /// <summary>
    /// Stands in for the real scheduler. Time is driven by the test instead of a
    /// clock, so the race between "cancel" and "already sent" can be staged exactly.
    /// </summary>
    private sealed class FakeReleaseGate : IReleaseGate
    {
        private readonly Dictionary<MouseButton, long> _pending = [];
        private readonly Dictionary<MouseButton, long> _pendingPresses = [];

        /// <summary>Releases sent to the system.</summary>
        public List<MouseButton> Sent { get; } = [];

        /// <summary>Presses sent to the system.</summary>
        public List<MouseButton> SentPresses { get; } = [];

        /// <summary>Everything sent, in order.</summary>
        public List<MouseEventKind> Log { get; } = [];

        public int FlushCount { get; private set; }

        public void Hold(MouseButton button, long deadlineUs) => _pending[button] = deadlineUs;

        public bool HoldPress(MouseButton button, long deadlineUs)
        {
            _pendingPresses[button] = deadlineUs;
            return true;
        }

        public bool Cancel(MouseButton button) => _pending.Remove(button) | _pendingPresses.Remove(button);

        public void FlushAll()
        {
            FlushCount++;
            _pendingPresses.Clear();
            foreach (var button in _pending.Keys.ToList()) Send(button);
        }

        /// <summary>Sends every event whose window has expired by <paramref name="nowUs"/>.</summary>
        public void Advance(long nowUs)
        {
            foreach (var (button, deadline) in _pending.ToList())
            {
                if (deadline <= nowUs) Send(button);
            }

            foreach (var (button, deadline) in _pendingPresses.ToList())
            {
                if (deadline <= nowUs)
                {
                    _pendingPresses.Remove(button);
                    SentPresses.Add(button);
                    Log.Add(MouseEventKind.Down);
                }
            }
        }

        private void Send(MouseButton button)
        {
            _pending.Remove(button);
            Sent.Add(button);
            Log.Add(MouseEventKind.Up);
        }

        public bool IsPending(MouseButton button) => _pending.ContainsKey(button);
    }

    private const int ReleaseDelayMs = 12;

    private static (ClickFilter filter, FakeReleaseGate gate) CreateFilter(
        FilterMode mode = FilterMode.Protect,
        int releaseDelayMs = ReleaseDelayMs)
    {
        var buttons = FilterSettings.CreateDefaultButtons();
        for (int i = 0; i < buttons.Length; i++)
            buttons[i] = buttons[i] with { ChatterThresholdMs = 25, ReleaseDelayMs = releaseDelayMs };

        var gate = new FakeReleaseGate();
        var filter = new ClickFilter(
            new FilterSettings { Mode = mode, Enabled = true, Buttons = buttons },
            releaseGate: gate);

        return (filter, gate);
    }

    private static MouseEvent Down(double atMs) => new(MouseButton.Left, MouseEventKind.Down, (long)(atMs * 1000), false);
    private static MouseEvent Up(double atMs) => new(MouseButton.Left, MouseEventKind.Up, (long)(atMs * 1000), false);

    [Fact]
    public void ARelease_IsHeldBackInsteadOfPassingStraightThrough()
    {
        var (filter, gate) = CreateFilter();

        filter.Handle(Down(0));
        var result = filter.Handle(Up(40));

        Assert.Equal(FilterAction.Defer, result.Action);
        Assert.Equal(FilterReason.ReleaseHeld, result.Reason);
        Assert.True(gate.IsPending(MouseButton.Left));
        Assert.Empty(gate.Sent);
    }

    [Fact]
    public void APressInsideTheWindow_CancelsTheReleaseAndTheHoldContinues()
    {
        var (filter, gate) = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(40));            // contact drops mid-hold
        var result = filter.Handle(Down(43));

        // Both events are swallowed, so as far as every application is concerned
        // the button was never let go.
        Assert.Equal(FilterAction.Suppress, result.Action);
        Assert.Equal(FilterReason.ReleaseRepaired, result.Reason);
        Assert.Empty(gate.Sent);
        Assert.False(gate.IsPending(MouseButton.Left));
        Assert.Equal(1, filter.Statistics[MouseButton.Left].ReleaseRepairs);
    }

    [Fact]
    public void AfterARepair_TheRealReleaseIsStillHandled()
    {
        var (filter, gate) = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(40));
        filter.Handle(Down(43));          // repaired

        var result = filter.Handle(Up(200));
        Assert.Equal(FilterAction.Defer, result.Action);

        gate.Advance(212_000);            // window expires
        Assert.Single(gate.Sent);

        // The hold lasted from 0 ms to 200 ms without a break, which is the point.
        Assert.Equal(200, filter.Statistics[MouseButton.Left].PressDuration.MaxMs, 1);
    }

    [Fact]
    public void APressAfterTheWindow_IsJudgedNormally()
    {
        var (filter, gate) = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(40));
        gate.Advance(52_000);             // the release already went out

        // 100 ms later: an ordinary second click, nothing to repair.
        var result = filter.Handle(Down(140));

        Assert.Equal(FilterAction.Pass, result.Action);
        Assert.Equal(0, filter.Statistics[MouseButton.Left].ReleaseRepairs);
    }

    [Fact]
    public void WhenTheReleaseAlreadyWentOut_AnImmediatePressIsStillChatter()
    {
        var (filter, gate) = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(40));
        gate.Advance(52_000);             // the scheduler won the race, the release went out

        // 13 ms after the physical release. The repair window is over, but no hand
        // presses again that fast, so the ordinary chatter rule still applies.
        var result = filter.Handle(Down(53));

        Assert.Equal(FilterAction.Suppress, result.Action);
        Assert.Equal(FilterReason.ChatterDown, result.Reason);

        // And its release must go too, or the system sees a release with no press.
        Assert.Equal(FilterAction.Suppress, filter.Handle(Up(70)).Action);
    }

    [Fact]
    public void AChatterPressStillHeld_IsSentAfterAllAndTheHoldResumes()
    {
        var (filter, gate) = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(40));            // contact drops mid-hold
        gate.Advance(52_000);             // longer than the window, so the release went out

        // The contact returns 13 ms after the drop: judged as chatter and swallowed...
        Assert.Equal(FilterReason.ChatterDown, filter.Handle(Down(53)).Reason);
        Assert.Empty(gate.SentPresses);

        // ...but the finger is still down 30 ms later, so this is a hold, not a bounce.
        gate.Advance(83_000);
        Assert.Single(gate.SentPresses);

        // The real release now belongs to the restored press and is handled normally.
        var result = filter.Handle(Up(500));
        Assert.Equal(FilterAction.Defer, result.Action);
        Assert.Equal(0, filter.Statistics[MouseButton.Left].OrphanSuppressed);

        gate.Advance(512_000);
        Assert.Equal([MouseEventKind.Up, MouseEventKind.Down, MouseEventKind.Up], gate.Log);
    }

    [Fact]
    public void AShortChatterPress_IsNeverRestored()
    {
        var (filter, gate) = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(40));
        gate.Advance(52_000);

        filter.Handle(Down(53));                                  // bounce
        Assert.Equal(FilterReason.OrphanUp, filter.Handle(Up(58)).Reason);

        gate.Advance(1_000_000);
        Assert.Empty(gate.SentPresses);
    }

    [Fact]
    public void WithTheDelaySetToZero_ReleasesPassStraightThrough()
    {
        var (filter, gate) = CreateFilter(releaseDelayMs: 0);

        filter.Handle(Down(0));
        var result = filter.Handle(Up(40));

        Assert.Equal(FilterAction.Pass, result.Action);
        Assert.False(gate.IsPending(MouseButton.Left));
    }

    [Fact]
    public void MonitorMode_NeverHoldsAReleaseBack()
    {
        var (filter, gate) = CreateFilter(mode: FilterMode.Monitor);

        filter.Handle(Down(0));
        var result = filter.Handle(Up(40));

        Assert.Equal(FilterAction.Pass, result.Action);
        Assert.False(gate.IsPending(MouseButton.Left));
    }

    [Fact]
    public void WithTheReleaseHoldOn_TheSystemStaysBalanced()
    {
        // Same guarantee as the plain filter, but now releases can also be delayed
        // or cancelled, which gives the state machine far more ways to go wrong.
        var (filter, gate) = CreateFilter();
        var random = new Random(912);

        bool systemSeesButtonDown = false;
        int sentAlready = 0;
        double t = 0;

        void DrainGate(double nowMs)
        {
            gate.Advance((long)(nowMs * 1000));
            while (sentAlready < gate.Log.Count)
            {
                if (gate.Log[sentAlready] == MouseEventKind.Up)
                {
                    Assert.True(systemSeesButtonDown, "a held release was sent while the system saw no press");
                    systemSeesButtonDown = false;
                }
                else
                {
                    Assert.False(systemSeesButtonDown, "a restored press was sent while the system saw a press");
                    systemSeesButtonDown = true;
                }
                sentAlready++;
            }
        }

        for (int i = 0; i < 20_000; i++)
        {
            t += random.Next(100) < 25 ? random.Next(1, 16) : random.Next(30, 400);
            DrainGate(t);

            var downResult = filter.Handle(Down(t));
            if (downResult.Action == FilterAction.Pass)
            {
                Assert.False(systemSeesButtonDown, $"step {i}: two presses in a row reached the system");
                systemSeesButtonDown = true;
            }

            t += random.Next(2, 250);
            DrainGate(t);

            var upResult = filter.Handle(Up(t));
            if (upResult.Action == FilterAction.Pass)
            {
                Assert.True(systemSeesButtonDown, $"step {i}: a release reached the system with no press");
                systemSeesButtonDown = false;
            }
        }

        // Whatever is still held back must be flushed on shutdown, leaving nothing pressed.
        gate.FlushAll();
        while (sentAlready < gate.Log.Count)
        {
            systemSeesButtonDown = false;
            sentAlready++;
        }

        Assert.False(systemSeesButtonDown, "the button was left pressed at the end of the sequence");
    }
}
