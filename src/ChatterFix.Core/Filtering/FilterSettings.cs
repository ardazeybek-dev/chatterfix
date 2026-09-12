namespace ChatterFix.Core.Filtering;

/// <summary>How the filter behaves.</summary>
public enum FilterMode
{
    /// <summary>Nothing is blocked; events are only measured. Thresholds are calibrated in this mode.</summary>
    Monitor,

    /// <summary>Faulty events are swallowed.</summary>
    Protect,
}

/// <summary>Filter settings for a single button.</summary>
public sealed record ButtonFilterSettings
{
    /// <summary>Whether this button is filtered at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// A press arriving less than this many milliseconds after a release counts as chatter.
    /// Even jitter clicking keeps releases about 40 ms apart, so 25 ms is a safe default.
    /// </summary>
    public int ChatterThresholdMs { get; init; } = 25;

    /// <summary>
    /// Hold a release back for this many milliseconds; if a new press arrives within
    /// the window, the connection dropped and both events are cancelled. 0 disables it.
    /// </summary>
    public int ReleaseDelayMs { get; init; }

    internal long ChatterThresholdUs => (long)ChatterThresholdMs * 1000;
    internal long ReleaseDelayUs => (long)ReleaseDelayMs * 1000;
}

/// <summary>
/// The complete filter configuration. It is immutable: changing a setting builds a
/// new instance that is swapped in atomically, so the hook thread never waits on a lock.
/// </summary>
public sealed record FilterSettings
{
    public FilterMode Mode { get; init; } = FilterMode.Protect;

    /// <summary>Master switch; when false nothing is blocked.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Per-button settings, in the same order as <see cref="MouseButton"/>.</summary>
    public ButtonFilterSettings[] Buttons { get; init; } = CreateDefaultButtons();

    public static ButtonFilterSettings[] CreateDefaultButtons() =>
    [
        new() { Enabled = true, ChatterThresholdMs = 25 },   // Left: where the fault almost always is
        new() { Enabled = true, ChatterThresholdMs = 25 },   // Right
        new() { Enabled = true, ChatterThresholdMs = 25 },   // Middle
        new() { Enabled = true, ChatterThresholdMs = 25 },   // Side 1
        new() { Enabled = true, ChatterThresholdMs = 25 },   // Side 2
    ];

    /// <summary>A configuration that measures without blocking anything.</summary>
    public static FilterSettings Monitoring() => new() { Mode = FilterMode.Monitor };
}
