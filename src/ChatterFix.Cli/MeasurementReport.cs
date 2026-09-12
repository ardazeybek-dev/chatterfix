using System.Text.Json;
using ChatterFix.Core;
using ChatterFix.Core.Diagnostics;
using ChatterFix.Core.Filtering;

namespace ChatterFix.Cli;

/// <summary>Saves the measurement as machine-readable JSON.</summary>
internal static class MeasurementReport
{
    public static void Write(string path, ClickFilter filter, DateTime startedAt, DateTime finishedAt)
    {
        var stats = filter.Statistics;
        var buttons = new List<object>();

        for (int i = 0; i < ClickStatistics.ButtonCount; i++)
        {
            var button = (MouseButton)i;
            var s = stats[button];
            if (s.Downs == 0) continue;

            buttons.Add(new
            {
                name = ClickStatistics.ButtonName(button),
                id = button.ToString(),
                downs = s.Downs,
                ups = s.Ups,
                chatterSuppressed = s.ChatterSuppressed,
                orphanSuppressed = s.OrphanSuppressed,
                releaseRepairs = s.ReleaseRepairs,
                releaseGap = Describe(s.ReleaseGap),
                pressDuration = Describe(s.PressDuration),
                suggestedThresholdMs = s.ReleaseGap.FindFirstGapUpperBoundMs(),
            });
        }

        var report = new
        {
            generatedAt = finishedAt,
            durationSeconds = Math.Round((finishedAt - startedAt).TotalSeconds, 1),
            mode = filter.Settings.Mode.ToString(),
            thresholdMs = filter.Settings.Buttons[0].ChatterThresholdMs,
            buttons,
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(report, options));
    }

    private static object Describe(IntervalHistogram histogram)
    {
        var counts = histogram.Snapshot();
        var buckets = new List<object>();

        for (int i = 0; i < IntervalHistogram.UpperBoundsMs.Length; i++)
        {
            if (counts[i] == 0) continue;

            int lower = i == 0 ? 0 : IntervalHistogram.UpperBoundsMs[i - 1];
            int upper = IntervalHistogram.UpperBoundsMs[i];

            buckets.Add(new
            {
                fromMs = lower,
                toMs = upper == int.MaxValue ? (int?)null : upper,
                count = counts[i],
            });
        }

        return new
        {
            total = histogram.Total,
            minMs = Math.Round(histogram.MinMs, 2),
            averageMs = Math.Round(histogram.AverageMs, 2),
            maxMs = Math.Round(histogram.MaxMs, 2),
            below10ms = histogram.CountBelow(10),
            below25ms = histogram.CountBelow(25),
            buckets,
        };
    }
}
