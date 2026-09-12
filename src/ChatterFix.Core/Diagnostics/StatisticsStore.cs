using System.Text.Json;

namespace ChatterFix.Core.Diagnostics;

/// <summary>
/// Keeps counters across restarts.
///
/// A failing switch shows itself over days, not minutes: the faults are rare, and
/// the interval that separates them from real clicks only becomes clear once
/// thousands of clicks have been recorded. Counters that reset at every launch
/// would never accumulate enough to answer the question.
/// </summary>
public static class StatisticsStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChatterFix",
        "statistics.json");

    private sealed record StoredStatistics(DateTimeOffset RecordingSince, ButtonStatistics.State[] Buttons);

    /// <summary>Writes the counters. Failures are swallowed: statistics must never break filtering.</summary>
    public static bool TrySave(ClickStatistics statistics, DateTimeOffset recordingSince, string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var stored = new StoredStatistics(recordingSince, statistics.CaptureState());
            string json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });

            // Write beside the target first: a crash mid-write would otherwise leave
            // a truncated file that loses every recorded click.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads counters into <paramref name="statistics"/> and returns the moment
    /// recording started. Returns now when there is nothing usable to load.
    /// </summary>
    public static DateTimeOffset Load(ClickStatistics statistics, string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return DateTimeOffset.Now;

            var stored = JsonSerializer.Deserialize<StoredStatistics>(File.ReadAllText(path));
            if (stored is null) return DateTimeOffset.Now;

            statistics.RestoreState(stored.Buttons);
            return stored.RecordingSince;
        }
        catch (Exception)
        {
            return DateTimeOffset.Now;
        }
    }

    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Nothing useful to do; the counters were reset in memory regardless.
        }
    }
}
