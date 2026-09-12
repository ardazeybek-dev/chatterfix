namespace ChatterFix.Core.Diagnostics;

/// <summary>One entry in the live event feed.</summary>
public readonly record struct EventLogEntry(
    long TimestampUs,
    MouseButton Button,
    MouseEventKind Kind,
    FilterAction Action,
    FilterReason Reason,
    long GapUs)
{
    public double GapMs => GapUs < 0 ? -1 : GapUs / 1000.0;
    public double TimestampSeconds => TimestampUs / 1_000_000.0;
}

/// <summary>
/// Ring buffer holding the most recent events. One writer (the hook thread),
/// many readers. It takes no lock; at worst a reader sees the newest entry
/// half-written, which is harmless because this feeds the display only.
/// </summary>
public sealed class EventRing(int capacity = 512)
{
    private readonly EventLogEntry[] _items = new EventLogEntry[capacity];
    private long _written;

    public int Capacity => _items.Length;

    public long TotalWritten => Interlocked.Read(ref _written);

    public void Add(in EventLogEntry entry)
    {
        long index = Interlocked.Increment(ref _written) - 1;
        _items[(int)(index % _items.Length)] = entry;
    }

    /// <summary>Returns the newest <paramref name="count"/> entries, newest first.</summary>
    public EventLogEntry[] TakeLatest(int count)
    {
        long written = Interlocked.Read(ref _written);
        int available = (int)Math.Min(written, _items.Length);
        int take = Math.Min(count, available);
        if (take <= 0) return [];

        var result = new EventLogEntry[take];
        for (int i = 0; i < take; i++)
        {
            long index = written - 1 - i;
            result[i] = _items[(int)(index % _items.Length)];
        }
        return result;
    }

    public void Clear() => Interlocked.Exchange(ref _written, 0);
}
