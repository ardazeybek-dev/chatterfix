namespace ChatterFix.Core.Diagnostics;

/// <summary>
/// Distributes click intervals into fixed buckets.
/// Written from the hook thread and read from the UI, so it never allocates and
/// never takes a lock. A reader may miss the most recent event or two, which is
/// acceptable for statistics.
/// </summary>
public sealed class IntervalHistogram
{
    /// <summary>Bucket upper bounds in milliseconds. The last bucket collects everything above.</summary>
    public static readonly int[] UpperBoundsMs =
        [1, 2, 3, 5, 7, 10, 15, 20, 25, 35, 50, 75, 100, 150, 250, int.MaxValue];

    private readonly long[] _counts = new long[UpperBoundsMs.Length];

    private long _total;
    private long _sumUs;
    private long _minUs = long.MaxValue;
    private long _maxUs = long.MinValue;

    public long Total => Interlocked.Read(ref _total);

    public double AverageMs
    {
        get
        {
            long total = Interlocked.Read(ref _total);
            return total == 0 ? 0 : Interlocked.Read(ref _sumUs) / 1000.0 / total;
        }
    }

    public double MinMs
    {
        get
        {
            long value = Interlocked.Read(ref _minUs);
            return value == long.MaxValue ? 0 : value / 1000.0;
        }
    }

    public double MaxMs
    {
        get
        {
            long value = Interlocked.Read(ref _maxUs);
            return value == long.MinValue ? 0 : value / 1000.0;
        }
    }

    public void Add(long intervalUs)
    {
        if (intervalUs < 0) return;

        int index = FindBucket(intervalUs);
        Interlocked.Increment(ref _counts[index]);
        Interlocked.Increment(ref _total);
        Interlocked.Add(ref _sumUs, intervalUs);

        // Lock-free min/max update.
        long current = Interlocked.Read(ref _minUs);
        while (intervalUs < current)
        {
            long previous = Interlocked.CompareExchange(ref _minUs, intervalUs, current);
            if (previous == current) break;
            current = previous;
        }

        current = Interlocked.Read(ref _maxUs);
        while (intervalUs > current)
        {
            long previous = Interlocked.CompareExchange(ref _maxUs, intervalUs, current);
            if (previous == current) break;
            current = previous;
        }
    }

    private static int FindBucket(long intervalUs)
    {
        double ms = intervalUs / 1000.0;
        for (int i = 0; i < UpperBoundsMs.Length; i++)
        {
            if (ms < UpperBoundsMs[i]) return i;
        }
        return UpperBoundsMs.Length - 1;
    }

    /// <summary>Returns a snapshot of the bucket counters.</summary>
    public long[] Snapshot()
    {
        var copy = new long[_counts.Length];
        for (int i = 0; i < _counts.Length; i++)
            copy[i] = Interlocked.Read(ref _counts[i]);
        return copy;
    }

    /// <summary>
    /// How many events fell below the given threshold in milliseconds.
    /// Used to estimate how much a candidate threshold would affect real clicks.
    /// </summary>
    public long CountBelow(double thresholdMs)
    {
        long sum = 0;
        for (int i = 0; i < UpperBoundsMs.Length; i++)
        {
            if (UpperBoundsMs[i] > thresholdMs) break;
            sum += Interlocked.Read(ref _counts[i]);
        }
        return sum;
    }

    /// <summary>
    /// Finds the first empty stretch after the lowest non-empty bucket, which is
    /// the gap between the faulty cluster and real clicks. Returns -1 when there
    /// is no such gap, meaning the two populations overlap.
    /// </summary>
    public int FindFirstGapUpperBoundMs()
    {
        var counts = Snapshot();

        int firstUsed = -1;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0) { firstUsed = i; break; }
        }
        if (firstUsed < 0) return -1;

        for (int i = firstUsed + 1; i < counts.Length; i++)
        {
            if (counts[i] == 0)
                return UpperBoundsMs[i - 1];
        }
        return -1;
    }

    public void Reset()
    {
        for (int i = 0; i < _counts.Length; i++) Interlocked.Exchange(ref _counts[i], 0);
        Interlocked.Exchange(ref _total, 0);
        Interlocked.Exchange(ref _sumUs, 0);
        Interlocked.Exchange(ref _minUs, long.MaxValue);
        Interlocked.Exchange(ref _maxUs, long.MinValue);
    }
}
