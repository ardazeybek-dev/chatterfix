namespace ChatterFix.Core;

/// <summary>Mouse buttons the filter can act on. The order is used as an array index and must not change.</summary>
public enum MouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4,
}

/// <summary>Direction of a button event.</summary>
public enum MouseEventKind
{
    Down,
    Up,
}

/// <summary>What the filter decided to do with an event.</summary>
public enum FilterAction
{
    /// <summary>The event reaches the system normally.</summary>
    Pass,

    /// <summary>The event is swallowed; no application ever sees it.</summary>
    Suppress,

    /// <summary>The event is held back and may be injected later (drop repair).</summary>
    Defer,
}

/// <summary>Why the filter reached its decision. Used for statistics and diagnostics.</summary>
public enum FilterReason
{
    /// <summary>A healthy event.</summary>
    Normal,

    /// <summary>A press that followed a release too fast for a human hand (chatter).</summary>
    ChatterDown,

    /// <summary>The release belonging to a swallowed press; dropped so the button cannot stick.</summary>
    OrphanUp,

    /// <summary>A held release cancelled because a new press arrived (drop repair).</summary>
    ReleaseRepaired,

    /// <summary>A release held back while we wait to see whether it was a real release.</summary>
    ReleaseHeld,

    /// <summary>An event produced by software rather than hardware; never filtered.</summary>
    Injected,
}

/// <summary>A single button event handed from the hook layer to the filter.</summary>
/// <param name="Button">Which button.</param>
/// <param name="Kind">Press or release.</param>
/// <param name="TimestampUs">Microseconds since the hook was installed.</param>
/// <param name="IsInjected">True when the event came from software instead of the physical device.</param>
public readonly record struct MouseEvent(
    MouseButton Button,
    MouseEventKind Kind,
    long TimestampUs,
    bool IsInjected)
{
    public bool IsDown => Kind == MouseEventKind.Down;
}

/// <summary>The filter's verdict on one event.</summary>
/// <param name="Action">What happens to the event.</param>
/// <param name="Reason">Why.</param>
/// <param name="GapUs">The interval that drove the decision, in microseconds; -1 when not applicable.</param>
public readonly record struct FilterResult(
    FilterAction Action,
    FilterReason Reason,
    long GapUs)
{
    public static FilterResult Normal(long gapUs) => new(FilterAction.Pass, FilterReason.Normal, gapUs);

    public double GapMs => GapUs < 0 ? -1 : GapUs / 1000.0;
}
