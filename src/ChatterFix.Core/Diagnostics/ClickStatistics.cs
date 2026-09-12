namespace ChatterFix.Core.Diagnostics;

/// <summary>Counters and interval distributions for a single button.</summary>
public sealed class ButtonStatistics
{
    /// <summary>Gap between a release and the next press. This is what exposes chatter.</summary>
    public IntervalHistogram ReleaseGap { get; } = new();

    /// <summary>How long the button stayed held (press to release).</summary>
    public IntervalHistogram PressDuration { get; } = new();

    private long _downs;
    private long _ups;
    private long _chatterSuppressed;
    private long _orphanSuppressed;
    private long _releaseRepairs;

    public long Downs => Interlocked.Read(ref _downs);
    public long Ups => Interlocked.Read(ref _ups);

    /// <summary>Presses swallowed because they were counted as chatter.</summary>
    public long ChatterSuppressed => Interlocked.Read(ref _chatterSuppressed);

    /// <summary>Releases swallowed because they belonged to a swallowed press.</summary>
    public long OrphanSuppressed => Interlocked.Read(ref _orphanSuppressed);

    /// <summary>Releases cancelled because they turned out to be a dropped connection.</summary>
    public long ReleaseRepairs => Interlocked.Read(ref _releaseRepairs);

    /// <summary>Share of physical presses that turned out to be faulty.</summary>
    public double ChatterRatePercent
    {
        get
        {
            long downs = Downs;
            return downs == 0 ? 0 : 100.0 * ChatterSuppressed / downs;
        }
    }

    internal void AddDown(long releaseGapUs)
    {
        Interlocked.Increment(ref _downs);
        if (releaseGapUs >= 0) ReleaseGap.Add(releaseGapUs);
    }

    internal void AddUp(long pressDurationUs)
    {
        Interlocked.Increment(ref _ups);
        if (pressDurationUs >= 0) PressDuration.Add(pressDurationUs);
    }

    internal void AddChatter() => Interlocked.Increment(ref _chatterSuppressed);
    internal void AddOrphan() => Interlocked.Increment(ref _orphanSuppressed);
    internal void AddReleaseRepair() => Interlocked.Increment(ref _releaseRepairs);

    public void Reset()
    {
        Interlocked.Exchange(ref _downs, 0);
        Interlocked.Exchange(ref _ups, 0);
        Interlocked.Exchange(ref _chatterSuppressed, 0);
        Interlocked.Exchange(ref _orphanSuppressed, 0);
        Interlocked.Exchange(ref _releaseRepairs, 0);
        ReleaseGap.Reset();
        PressDuration.Reset();
    }
}

/// <summary>Statistics for every button.</summary>
public sealed class ClickStatistics
{
    public const int ButtonCount = 5;

    private readonly ButtonStatistics[] _buttons;

    public ClickStatistics()
    {
        _buttons = new ButtonStatistics[ButtonCount];
        for (int i = 0; i < ButtonCount; i++) _buttons[i] = new ButtonStatistics();
    }

    public ButtonStatistics this[MouseButton button] => _buttons[(int)button];

    public IReadOnlyList<ButtonStatistics> All => _buttons;

    public long TotalChatterSuppressed
    {
        get
        {
            long sum = 0;
            foreach (var button in _buttons) sum += button.ChatterSuppressed;
            return sum;
        }
    }

    public long TotalReleaseRepairs
    {
        get
        {
            long sum = 0;
            foreach (var button in _buttons) sum += button.ReleaseRepairs;
            return sum;
        }
    }

    public void Reset()
    {
        foreach (var button in _buttons) button.Reset();
    }

    public static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Left => "Left",
        MouseButton.Right => "Right",
        MouseButton.Middle => "Middle",
        MouseButton.X1 => "Side 1",
        MouseButton.X2 => "Side 2",
        _ => button.ToString(),
    };
}
