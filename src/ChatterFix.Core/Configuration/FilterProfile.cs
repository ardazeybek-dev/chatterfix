using ChatterFix.Core.Diagnostics;
using ChatterFix.Core.Filtering;

namespace ChatterFix.Core.Configuration;

/// <summary>
/// A set of thresholds that applies while a particular application is in the foreground.
///
/// One threshold cannot serve every situation. On the desktop nobody clicks twice
/// within 40 ms, so a wide threshold is free of risk and catches far more faults.
/// In a game the same person may reach 30 clicks a second, where 40 ms would start
/// eating real clicks. So the thresholds follow whatever has focus.
/// </summary>
public sealed class FilterProfile
{
    public string Name { get; set; } = "Desktop";

    /// <summary>
    /// Process names this profile applies to, without the .exe. An empty list marks
    /// the fallback profile used whenever nothing else matches.
    /// </summary>
    public string[] ProcessNames { get; set; } = [];

    public int ChatterThresholdMs { get; set; } = 35;

    public int ReleaseDelayMs { get; set; } = 35;

    public bool[] ButtonsEnabled { get; set; } = [true, true, true, true, true];

    public bool IsFallback => ProcessNames.Length == 0;

    public bool Matches(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;

        foreach (string candidate in ProcessNames)
        {
            if (string.Equals(candidate, processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public FilterSettings ToFilterSettings(bool enabled)
    {
        var buttons = new ButtonFilterSettings[ClickStatistics.ButtonCount];
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i] = new ButtonFilterSettings
            {
                Enabled = i < ButtonsEnabled.Length && ButtonsEnabled[i],
                ChatterThresholdMs = ChatterThresholdMs,
                ReleaseDelayMs = ReleaseDelayMs,
            };
        }

        return new FilterSettings
        {
            Mode = FilterMode.Protect,
            Enabled = enabled,
            Buttons = buttons,
        };
    }

    public FilterProfile Sanitised()
    {
        ChatterThresholdMs = Math.Clamp(ChatterThresholdMs, 1, 100);
        ReleaseDelayMs = Math.Clamp(ReleaseDelayMs, 0, 50);

        if (ButtonsEnabled.Length != ClickStatistics.ButtonCount)
        {
            var fixedButtons = new bool[ClickStatistics.ButtonCount];
            for (int i = 0; i < fixedButtons.Length; i++)
                fixedButtons[i] = i >= ButtonsEnabled.Length || ButtonsEnabled[i];
            ButtonsEnabled = fixedButtons;
        }

        if (string.IsNullOrWhiteSpace(Name)) Name = "Unnamed";

        return this;
    }

    public static List<FilterProfile> CreateDefaults() =>
    [
        new()
        {
            Name = "Desktop",
            ProcessNames = [],
            // Real double clicks sit above 40 ms, while a worn contact breaks for
            // 15-30 ms in the middle of a hold. The release window spans the whole
            // threshold, so such a break is mended into one unbroken hold instead of
            // the press being swallowed: a drag survives it, and a phantom click is
            // folded into the click before it rather than reaching the application.
            ChatterThresholdMs = 35,
            ReleaseDelayMs = 35,
        },
        new()
        {
            Name = "Fast clicking",
            ProcessNames = ["javaw", "java", "Minecraft", "LunarClient", "BadlionClient", "PrismLauncher", "ATLauncher"],
            // Deliberate fast clicking reaches 30 per second, leaving roughly 15 ms
            // between a release and the next press. The threshold has to stay under that.
            ChatterThresholdMs = 12,
            ReleaseDelayMs = 8,
        },
    ];
}
