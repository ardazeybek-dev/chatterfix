using System.Text.Json;
using System.Text.Json.Serialization;
using ChatterFix.Core.Diagnostics;
using ChatterFix.Core.Filtering;

namespace ChatterFix.Core.Configuration;

/// <summary>
/// Settings as they are stored on disk, in a form a person can read and edit.
/// Kept separate from <see cref="FilterSettings"/> so the runtime type can stay
/// immutable and allocation-free on the hook path.
/// </summary>
public sealed class AppConfiguration
{
    /// <summary>Master switch. When off the hook stays installed but blocks nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Start with Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>A press this soon after a release counts as a hardware fault.</summary>
    public int ChatterThresholdMs { get; set; } = 25;

    /// <summary>How long a release is held to see whether the contact merely bounced. 0 disables it.</summary>
    public int ReleaseDelayMs { get; set; } = 12;

    /// <summary>Which buttons are filtered, in <see cref="MouseButton"/> order.</summary>
    public bool[] ButtonsEnabled { get; set; } = [true, true, true, true, true];

    /// <summary>Show a balloon notification the first time a fault is blocked in a session.</summary>
    public bool NotifyOnFirstBlock { get; set; } = true;

    [JsonIgnore]
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChatterFix",
        "config.json");

    public FilterSettings ToFilterSettings()
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
            Enabled = Enabled,
            Buttons = buttons,
        };
    }

    /// <summary>
    /// Loads the configuration, falling back to defaults whenever the file is missing
    /// or unreadable. A broken config must never stop the filter from running.
    /// </summary>
    public static AppConfiguration Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return new AppConfiguration();

            var config = JsonSerializer.Deserialize<AppConfiguration>(File.ReadAllText(path));
            return config is null ? new AppConfiguration() : config.Sanitised();
        }
        catch (Exception)
        {
            return new AppConfiguration();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;

        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    /// <summary>
    /// Clamps hand-edited values into a range that cannot break clicking.
    /// A threshold of, say, 300 ms would swallow ordinary double clicks.
    /// </summary>
    public AppConfiguration Sanitised()
    {
        ChatterThresholdMs = Math.Clamp(ChatterThresholdMs, 1, 100);
        ReleaseDelayMs = Math.Clamp(ReleaseDelayMs, 0, 50);

        if (ButtonsEnabled.Length != ClickStatistics.ButtonCount)
        {
            var fixedButtons = new bool[ClickStatistics.ButtonCount];
            for (int i = 0; i < fixedButtons.Length; i++)
                fixedButtons[i] = i < ButtonsEnabled.Length ? ButtonsEnabled[i] : true;
            ButtonsEnabled = fixedButtons;
        }

        return this;
    }
}
