using ChatterFix.Core.Filtering;

namespace ChatterFix.Core.Native;

/// <summary>
/// Sends held-back releases once their window expires, and restores presses that
/// were swallowed as chatter but turned out to be held.
///
/// Windows timers are far too coarse for this: an ordinary wait rounds to about
/// 15 ms, which is longer than the window itself. So the scheduler raises the
/// system timer resolution while it runs and spins through the final stretch.
///
/// Two threads meet here — the hook cancels a release at the same moment this
/// thread may be sending it — so ownership of each pending release is claimed
/// with an atomic swap. Exactly one side wins, and the caller is told which.
/// </summary>
public sealed class ReleaseScheduler : IReleaseGate, IDisposable
{
    private const long NoDeadline = 0;

    /// <summary>Below this, spin instead of sleeping; a wait this short is not accurate.</summary>
    private const long SpinWindowUs = 2000;

    private readonly long[] _deadlinesUs = new long[Diagnostics.ClickStatistics.ButtonCount];

    // Presses live in their own slots: if one array carried both, the scheduler could
    // claim a release while the hook reuses the slot for a press, and send the wrong event.
    private readonly long[] _pressDeadlinesUs = new long[Diagnostics.ClickStatistics.ButtonCount];

    private readonly Func<long> _nowUs;
    private readonly Action<MouseButton> _sendRelease;
    private readonly Action<MouseButton>? _sendPress;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    private volatile bool _running = true;
    private bool _timerResolutionRaised;

    private long _sentReleases;
    private long _cancelledReleases;

    public ReleaseScheduler(Func<long> nowUs, Action<MouseButton> sendRelease, Action<MouseButton>? sendPress = null)
    {
        _nowUs = nowUs ?? throw new ArgumentNullException(nameof(nowUs));
        _sendRelease = sendRelease ?? throw new ArgumentNullException(nameof(sendRelease));
        _sendPress = sendPress;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ChatterFix-Release",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>Releases that waited out their window and were sent to the system.</summary>
    public long SentReleases => Interlocked.Read(ref _sentReleases);

    /// <summary>Releases cancelled because the button turned out to still be held.</summary>
    public long CancelledReleases => Interlocked.Read(ref _cancelledReleases);

    public void Hold(MouseButton button, long deadlineUs)
    {
        if (deadlineUs == NoDeadline) deadlineUs = 1; // 0 is the "nothing pending" marker
        Interlocked.Exchange(ref _deadlinesUs[(int)button], deadlineUs);
        _wake.Set();
    }

    public bool HoldPress(MouseButton button, long deadlineUs)
    {
        if (_sendPress is null) return false;

        if (deadlineUs == NoDeadline) deadlineUs = 1;
        Interlocked.Exchange(ref _pressDeadlinesUs[(int)button], deadlineUs);
        _wake.Set();
        return true;
    }

    public bool Cancel(MouseButton button)
    {
        bool cancelled = Interlocked.Exchange(ref _deadlinesUs[(int)button], NoDeadline) != NoDeadline;
        if (cancelled) Interlocked.Increment(ref _cancelledReleases);

        // Only one of the two can be pending for a button, but clear both regardless.
        cancelled |= Interlocked.Exchange(ref _pressDeadlinesUs[(int)button], NoDeadline) != NoDeadline;
        return cancelled;
    }

    public void FlushAll()
    {
        for (int i = 0; i < _deadlinesUs.Length; i++)
        {
            // A press sent now would have no release to follow it, so it is dropped.
            Interlocked.Exchange(ref _pressDeadlinesUs[i], NoDeadline);

            if (Interlocked.Exchange(ref _deadlinesUs[i], NoDeadline) != NoDeadline)
                SendRelease((MouseButton)i);
        }
    }

    private void Run()
    {
        // Ask Windows for 1 ms timer resolution; without it a short wait overshoots badly.
        _timerResolutionRaised = NativeMethods.TimeBeginPeriod(1) == 0;

        try
        {
            while (_running)
            {
                long nearest = FindNearestDeadline();

                if (nearest == long.MaxValue)
                {
                    _wake.WaitOne(50); // nothing pending; wake on the next hold
                    continue;
                }

                long remainingUs = nearest - _nowUs();

                if (remainingUs > SpinWindowUs)
                {
                    // Sleep most of the way, leaving the spin window for accuracy.
                    int waitMs = (int)((remainingUs - SpinWindowUs) / 1000);
                    if (waitMs > 0) _wake.WaitOne(waitMs);
                }
                else if (remainingUs > 0)
                {
                    while (_running && _nowUs() < nearest)
                        Thread.SpinWait(40);
                }

                SendExpired();
            }
        }
        finally
        {
            FlushAll(); // never leave a button held down on shutdown
            if (_timerResolutionRaised) NativeMethods.TimeEndPeriod(1);
        }
    }

    private long FindNearestDeadline()
    {
        long nearest = long.MaxValue;
        for (int i = 0; i < _deadlinesUs.Length; i++)
        {
            long deadline = Interlocked.Read(ref _deadlinesUs[i]);
            if (deadline != NoDeadline && deadline < nearest) nearest = deadline;

            long pressDeadline = Interlocked.Read(ref _pressDeadlinesUs[i]);
            if (pressDeadline != NoDeadline && pressDeadline < nearest) nearest = pressDeadline;
        }
        return nearest;
    }

    private void SendExpired()
    {
        long now = _nowUs();

        for (int i = 0; i < _deadlinesUs.Length; i++)
        {
            // Claim each event. If the hook cancelled it first, the swap fails and we skip it.
            if (TryClaim(_deadlinesUs, i, now))
                SendRelease((MouseButton)i);

            if (TryClaim(_pressDeadlinesUs, i, now))
                _sendPress!((MouseButton)i);
        }
    }

    private static bool TryClaim(long[] deadlines, int index, long now)
    {
        long deadline = Interlocked.Read(ref deadlines[index]);
        if (deadline == NoDeadline || deadline > now) return false;

        return Interlocked.CompareExchange(ref deadlines[index], NoDeadline, deadline) == deadline;
    }

    private void SendRelease(MouseButton button)
    {
        Interlocked.Increment(ref _sentReleases);
        _sendRelease(button);
    }

    public void Dispose()
    {
        if (!_running) return;

        _running = false;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
