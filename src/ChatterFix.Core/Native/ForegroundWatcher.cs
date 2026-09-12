using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatterFix.Core.Native;

/// <summary>
/// Reports which application currently has focus, so thresholds can follow it.
///
/// The lookup happens on its own timer rather than inside the hook: resolving a
/// window to a process name costs far more than the hook callback is allowed to
/// spend, and focus changes are rare compared to clicks. The hook only ever reads
/// settings that were already prepared here.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();

    private string? _currentProcessName;
    private uint _currentProcessId;
    private bool _disposed;

    /// <summary>Raised when focus moves to a different process. Never raised for the same process twice.</summary>
    public event Action<string?>? ForegroundChanged;

    public ForegroundWatcher(TimeSpan? interval = null)
    {
        var period = interval ?? TimeSpan.FromMilliseconds(400);
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, period);
    }

    public string? CurrentProcessName
    {
        get { lock (_gate) return _currentProcessName; }
    }

    private void Poll()
    {
        if (_disposed) return;

        try
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return;

            GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0) return;

            // Resolving the name is the expensive half, so skip it while focus stays put.
            lock (_gate)
            {
                if (processId == _currentProcessId) return;
            }

            string? name = TryGetProcessName(processId);

            string? previous;
            lock (_gate)
            {
                previous = _currentProcessName;
                _currentProcessId = processId;
                _currentProcessName = name;
            }

            if (!string.Equals(previous, name, StringComparison.OrdinalIgnoreCase))
                ForegroundChanged?.Invoke(name);
        }
        catch (Exception)
        {
            // Focus tracking is a convenience; if it fails the fallback profile stays in force.
        }
    }

    private static string? TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // Elevated or already-exited processes cannot be opened from here.
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
