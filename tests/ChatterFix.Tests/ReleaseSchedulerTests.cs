using ChatterFix.Core;
using ChatterFix.Core.Native;

namespace ChatterFix.Tests;

/// <summary>
/// Exercises the real scheduler with real threads and the real clock. These tests
/// are about timing, so they assert on behaviour that must hold rather than on
/// exact microseconds: a release must never go out early, and must not run late
/// enough to be felt.
/// </summary>
public class ReleaseSchedulerTests
{
    private sealed class Recorder
    {
        private readonly List<(MouseButton Button, long SentAtUs)> _sent = [];

        public void Record(MouseButton button)
        {
            lock (_sent) _sent.Add((button, MonotonicClock.NowMicroseconds()));
        }

        public (MouseButton Button, long SentAtUs)[] Snapshot()
        {
            lock (_sent) return [.. _sent];
        }
    }

    [Fact]
    public void APendingRelease_IsSentOnceItsWindowExpires()
    {
        var recorder = new Recorder();
        using var scheduler = new ReleaseScheduler(MonotonicClock.NowMicroseconds, recorder.Record);

        long deadline = MonotonicClock.NowMicroseconds() + 12_000;
        scheduler.Hold(MouseButton.Left, deadline);

        Thread.Sleep(200);

        var sent = recorder.Snapshot();
        Assert.Single(sent);
        Assert.Equal(MouseButton.Left, sent[0].Button);

        long lateByUs = sent[0].SentAtUs - deadline;

        // Early would be wrong: the repair window would not have been given its full time.
        Assert.True(lateByUs >= -500, $"the release went out {(-lateByUs) / 1000.0:F1} ms early");

        // Late is tolerable but must stay imperceptible. Windows timers are coarse
        // without the resolution bump, so this also proves the bump took effect.
        Assert.True(lateByUs < 15_000, $"the release was {lateByUs / 1000.0:F1} ms late");
    }

    [Fact]
    public void AScheduledPress_IsSentOnTheSameButtonAndNotAsARelease()
    {
        var releases = new Recorder();
        var presses = new Recorder();
        using var scheduler = new ReleaseScheduler(MonotonicClock.NowMicroseconds, releases.Record, presses.Record);

        Assert.True(scheduler.HoldPress(MouseButton.Right, MonotonicClock.NowMicroseconds() + 10_000));
        Thread.Sleep(150);

        Assert.Empty(releases.Snapshot());
        Assert.Equal(MouseButton.Right, Assert.Single(presses.Snapshot()).Button);
    }

    [Fact]
    public void WithoutAPressSender_NothingIsScheduled_AndShutdownNeverSendsAPress()
    {
        var recorder = new Recorder();
        var noPresses = new ReleaseScheduler(MonotonicClock.NowMicroseconds, recorder.Record);
        Assert.False(noPresses.HoldPress(MouseButton.Left, 1));
        noPresses.Dispose();

        var presses = new Recorder();
        var scheduler = new ReleaseScheduler(MonotonicClock.NowMicroseconds, recorder.Record, presses.Record);
        scheduler.HoldPress(MouseButton.Left, MonotonicClock.NowMicroseconds() + 10_000_000);
        scheduler.Dispose();

        // A press sent on the way out would leave the button stuck down.
        Assert.Empty(presses.Snapshot());
    }

    [Fact]
    public void ACancelledRelease_IsNeverSent()
    {
        var recorder = new Recorder();
        using var scheduler = new ReleaseScheduler(MonotonicClock.NowMicroseconds, recorder.Record);

        scheduler.Hold(MouseButton.Left, MonotonicClock.NowMicroseconds() + 30_000);
        Assert.True(scheduler.Cancel(MouseButton.Left));

        Thread.Sleep(150);

        Assert.Empty(recorder.Snapshot());
        Assert.Equal(1, scheduler.CancelledReleases);
    }

    [Fact]
    public void CancellingAfterTheReleaseWentOut_ReportsFalse()
    {
        var recorder = new Recorder();
        using var scheduler = new ReleaseScheduler(MonotonicClock.NowMicroseconds, recorder.Record);

        scheduler.Hold(MouseButton.Left, MonotonicClock.NowMicroseconds() + 5_000);
        Thread.Sleep(150); // let it expire

        // The caller must be told the button is already up, so it can stop treating
        // the next press as a repair.
        Assert.False(scheduler.Cancel(MouseButton.Left));
        Assert.Single(recorder.Snapshot());
    }

    [Fact]
    public void Buttons_AreHeldIndependently()
    {
        var recorder = new Recorder();
        using var scheduler = new ReleaseScheduler(MonotonicClock.NowMicroseconds, recorder.Record);

        long now = MonotonicClock.NowMicroseconds();
        scheduler.Hold(MouseButton.Left, now + 10_000);
        scheduler.Hold(MouseButton.Right, now + 30_000);

        Assert.True(scheduler.Cancel(MouseButton.Left));

        Thread.Sleep(200);

        var sent = recorder.Snapshot();
        Assert.Single(sent);
        Assert.Equal(MouseButton.Right, sent[0].Button);
    }

    [Fact]
    public void Disposing_SendsEveryPendingRelease()
    {
        // If the process exits while a release is held, the button would stay down
        // for every application on the system. Shutdown must always flush.
        var recorder = new Recorder();
        var scheduler = new ReleaseScheduler(MonotonicClock.NowMicroseconds, recorder.Record);

        long now = MonotonicClock.NowMicroseconds();
        scheduler.Hold(MouseButton.Left, now + 10_000_000);  // far in the future
        scheduler.Hold(MouseButton.Right, now + 10_000_000);

        scheduler.Dispose();

        var sent = recorder.Snapshot();
        Assert.Equal(2, sent.Length);
    }
}
