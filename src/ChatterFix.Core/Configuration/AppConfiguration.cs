using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatterFix.Core.Configuration;

/// <summary>
/// Settings as they are stored on disk, in a form a person can read and edit.
/// Kept separate from the runtime types so those can stay immutable and
/// allocation-free on the hook path.
/// </summary>
public sealed class AppConfiguration
{
    /// <summary>Master switch. When off the hook stays installed but blocks nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Start with Windows.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Show a notification the first time a fault is blocked in a session.</summary>
    public bool NotifyOnFirstBlock { get; set; } = true;

    /// <summary>Thresholds per foreground application. The profile with no process names is the fallback.</summary>
    public List<FilterProfile> Profiles { get; set; } = FilterProfile.CreateDefaults();

    [JsonIgnore]
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChatterFix",
        "config.json");

    /// <summary>Picks the profile for the given foreground process, falling back when nothing matches.</summary>
    public FilterProfile ResolveProfile(string? processName)
    {
        foreach (var profile in Profiles)
        {
            if (profile.Matches(processName)) return profile;
        }

        foreach (var profile in Profiles)
        {
            if (profile.IsFallback) return profile;
        }

        return Profiles.Count > 0 ? Profiles[0] : new FilterProfile();
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
    /// Repairs hand-edited values. Thresholds are clamped into a range that cannot
    /// break clicking, and a config with no fallback profile gets one, otherwise
    /// every application outside the named ones would go unfiltered.
    /// </summary>
    public AppConfiguration Sanitised()
    {
        if (Profiles.Count == 0)
        {
            Profiles = FilterProfile.CreateDefaults();
            return this;
        }

        foreach (var profile in Profiles) profile.Sanitised();

        bool hasFallback = Profiles.Exists(p => p.IsFallback);
        if (!hasFallback) Profiles.Add(new FilterProfile { Name = "Desktop", ProcessNames = [] });

        return this;
    }
}
