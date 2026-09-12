using ChatterFix.Core.Diagnostics;

namespace ChatterFix.Core.Filtering;

/// <summary>
/// The decision engine: for every mouse button event it decides whether the event
/// reaches the system or is swallowed. It is pure logic — it knows nothing about
/// Win32 and takes its clock from the caller, so it can be tested without a mouse.
///
/// The core rule: a press that follows a release faster than a human hand can manage
/// is a hardware fault (chatter) and gets swallowed. The matching release is swallowed
/// too; otherwise applications see a release with no press and the button sticks.
/// </summary>
public sealed class ClickFilter : IMouseEventSink
{
    private readonly ClickStatistics _statistics;
    private readonly EventRing _events;
    private readonly ButtonState[] _states;

    private volatile FilterSettings _settings;

    public ClickFilter(FilterSettings? settings = null, ClickStatistics? statistics = null, EventRing? events = null)
    {
        _settings = settings ?? new FilterSettings();
        _statistics = statistics ?? new ClickStatistics();
        _events = events ?? new EventRing();

        _states = new ButtonState[ClickStatistics.ButtonCount];
        ResetState();
    }

    public ClickStatistics Statistics => _statistics;

    public EventRing Events => _events;

    public FilterSettings Settings => _settings;

    /// <summary>Swaps settings atomically; the hook thread never blocks on this.</summary>
    public void UpdateSettings(FilterSettings settings)
        => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>Clears per-button state, used at startup and when the filter is toggled.</summary>
    public void ResetState()
    {
        for (int i = 0; i < _states.Length; i++)
        {
            _states[i].LastDownUs = -1;
            _states[i].LastUpUs = -1;
            _states[i].SuppressedDownPending = false;
        }
    }

    public FilterResult Handle(in MouseEvent e)
    {
        var settings = _settings;
        ref var state = ref _states[(int)e.Button];

        FilterResult result;

        if (e.IsInjected)
        {
            // A click produced by software cannot be a hardware fault.
            result = new FilterResult(FilterAction.Pass, FilterReason.Injected, -1);
        }
        else if (e.IsDown)
        {
            result = HandleDown(ref state, in e, settings);
        }
        else
        {
            result = HandleUp(ref state, in e, settings);
        }

        _events.Add(new EventLogEntry(e.TimestampUs, e.Button, e.Kind, result.Action, result.Reason, result.GapUs));
        return result;
    }

    private FilterResult HandleDown(ref ButtonState state, in MouseEvent e, FilterSettings settings)
    {
        // The measurement that exposes chatter is the gap since the last release.
        // Comparing press to press would miss a fault that follows a two-second
        // hold, because the presses themselves are far apart.
        long releaseGapUs = state.LastUpUs >= 0 ? e.TimestampUs - state.LastUpUs : -1;

        var buttonStats = _statistics[e.Button];
        buttonStats.AddDown(releaseGapUs);

        var buttonSettings = settings.Buttons[(int)e.Button];

        bool isChatter =
            settings.Enabled
            && settings.Mode == FilterMode.Protect
            && buttonSettings.Enabled
            && releaseGapUs >= 0
            && releaseGapUs < buttonSettings.ChatterThresholdUs;

        if (isChatter)
        {
            // This press is swallowed, so its release must be swallowed as well.
            state.SuppressedDownPending = true;
            buttonStats.AddChatter();
            return new FilterResult(FilterAction.Suppress, FilterReason.ChatterDown, releaseGapUs);
        }

        state.LastDownUs = e.TimestampUs;
        return FilterResult.Normal(releaseGapUs);
    }

    private FilterResult HandleUp(ref ButtonState state, in MouseEvent e, FilterSettings settings)
    {
        if (state.SuppressedDownPending)
        {
            state.SuppressedDownPending = false;

            // The physical release did happen, so the next chatter check measures from here.
            state.LastUpUs = e.TimestampUs;

            _statistics[e.Button].AddOrphan();
            return new FilterResult(FilterAction.Suppress, FilterReason.OrphanUp, -1);
        }

        long pressDurationUs = state.LastDownUs >= 0 ? e.TimestampUs - state.LastDownUs : -1;

        _statistics[e.Button].AddUp(pressDurationUs);
        state.LastUpUs = e.TimestampUs;

        return FilterResult.Normal(pressDurationUs);
    }

    private struct ButtonState
    {
        public long LastDownUs;
        public long LastUpUs;

        /// <summary>The last press was swallowed, so its release will be swallowed too.</summary>
        public bool SuppressedDownPending;
    }
}
