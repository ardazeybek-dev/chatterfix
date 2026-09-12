using System.Diagnostics;

namespace ChatterFix.Core;

/// <summary>
/// One shared clock for the whole process, in microseconds since startup.
///
/// The hook and the release scheduler compare timestamps with each other, so they
/// must read the same clock. Wall-clock time is unusable here: it jumps with NTP
/// corrections and only ticks every 15 ms, while the intervals being measured are
/// single-digit milliseconds.
/// </summary>
public static class MonotonicClock
{
    private static readonly long EpochTicks = Stopwatch.GetTimestamp();
    private static readonly double TicksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;

    public static long NowMicroseconds() => (long)((Stopwatch.GetTimestamp() - EpochTicks) * TicksToMicroseconds);
}
