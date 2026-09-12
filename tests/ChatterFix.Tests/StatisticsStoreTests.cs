using ChatterFix.Core;
using ChatterFix.Core.Diagnostics;
using ChatterFix.Core.Filtering;

namespace ChatterFix.Tests;

public class StatisticsStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"chatterfix-stats-{Guid.NewGuid():N}.json");

    /// <summary>Drives a filter with a mix of healthy clicks and faults, to produce real counters.</summary>
    private static ClickFilter BuildFilterWithHistory()
    {
        var buttons = FilterSettings.CreateDefaultButtons();
        var filter = new ClickFilter(new FilterSettings { Mode = FilterMode.Protect, Buttons = buttons });

        double t = 0;
        for (int i = 0; i < 20; i++)
        {
            filter.Handle(new MouseEvent(MouseButton.Left, MouseEventKind.Down, (long)(t * 1000), false));
            filter.Handle(new MouseEvent(MouseButton.Left, MouseEventKind.Up, (long)((t + 40) * 1000), false));
            t += 200;
        }

        // Two faults: a press 3 ms after a release, twice.
        for (int i = 0; i < 2; i++)
        {
            filter.Handle(new MouseEvent(MouseButton.Left, MouseEventKind.Down, (long)(t * 1000), false));
            filter.Handle(new MouseEvent(MouseButton.Left, MouseEventKind.Up, (long)((t + 3) * 1000), false));
            t += 200;
        }

        return filter;
    }

    [Fact]
    public void CountersSurviveARestart()
    {
        string path = TempPath();

        try
        {
            var original = BuildFilterWithHistory();
            var since = DateTimeOffset.Now.AddDays(-3);

            Assert.True(StatisticsStore.TrySave(original.Statistics, since, path));

            var restored = new ClickStatistics();
            var restoredSince = StatisticsStore.Load(restored, path);

            var before = original.Statistics[MouseButton.Left];
            var after = restored[MouseButton.Left];

            Assert.Equal(before.Downs, after.Downs);
            Assert.Equal(before.Ups, after.Ups);
            Assert.Equal(before.ChatterSuppressed, after.ChatterSuppressed);
            Assert.Equal(before.ReleaseGap.Total, after.ReleaseGap.Total);
            Assert.Equal(before.ReleaseGap.MinMs, after.ReleaseGap.MinMs, 2);
            Assert.Equal(before.ReleaseGap.AverageMs, after.ReleaseGap.AverageMs, 2);
            Assert.Equal(since.ToUnixTimeSeconds(), restoredSince.ToUnixTimeSeconds());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RestoredCountersKeepCountingFromWhereTheyLeftOff()
    {
        string path = TempPath();

        try
        {
            var original = BuildFilterWithHistory();
            long downsBefore = original.Statistics[MouseButton.Left].Downs;
            StatisticsStore.TrySave(original.Statistics, DateTimeOffset.Now, path);

            var statistics = new ClickStatistics();
            StatisticsStore.Load(statistics, path);

            var resumed = new ClickFilter(new FilterSettings(), statistics);
            resumed.Handle(new MouseEvent(MouseButton.Left, MouseEventKind.Down, 100_000_000, false));

            Assert.Equal(downsBefore + 1, statistics[MouseButton.Left].Downs);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableFileLoadsAsAFreshStart()
    {
        string path = TempPath();

        try
        {
            File.WriteAllText(path, "not json at all");

            var statistics = new ClickStatistics();
            var since = StatisticsStore.Load(statistics, path);

            Assert.Equal(0, statistics[MouseButton.Left].Downs);
            Assert.True((DateTimeOffset.Now - since).TotalSeconds < 5);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileLoadsAsAFreshStart()
    {
        var statistics = new ClickStatistics();

        var since = StatisticsStore.Load(statistics, TempPath());

        Assert.Equal(0, statistics[MouseButton.Left].Downs);
        Assert.True((DateTimeOffset.Now - since).TotalSeconds < 5);
    }

    [Fact]
    public void AHistogramSavedWithADifferentBucketLayoutIsIgnored()
    {
        // Counts from an older build would land in the wrong buckets and quietly
        // corrupt the picture the histogram is meant to give.
        var histogram = new IntervalHistogram();
        histogram.Add(5_000);

        histogram.RestoreState(new IntervalHistogram.State([1, 2, 3], 6, 1000, 10, 100));

        Assert.Equal(1, histogram.Total);
    }

    [Fact]
    public void DeletingTheStoreRemovesTheFile()
    {
        string path = TempPath();

        StatisticsStore.TrySave(new ClickStatistics(), DateTimeOffset.Now, path);
        Assert.True(File.Exists(path));

        StatisticsStore.Delete(path);
        Assert.False(File.Exists(path));
    }
}
