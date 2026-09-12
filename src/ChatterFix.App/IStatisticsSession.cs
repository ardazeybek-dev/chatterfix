using ChatterFix.Core.Filtering;

namespace ChatterFix.App;

/// <summary>
/// What the statistics window needs from the running application. Keeping it to an
/// interface means the window cannot reach into the tray application and, more
/// importantly, that resetting counters clears the saved file as well as memory.
/// </summary>
internal interface IStatisticsSession
{
    ClickFilter Filter { get; }

    /// <summary>When the current set of counters started accumulating.</summary>
    DateTimeOffset RecordingSince { get; }

    void ResetCounters();
}
