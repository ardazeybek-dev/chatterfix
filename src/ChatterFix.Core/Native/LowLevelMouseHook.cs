using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChatterFix.Core.Native;

/// <summary>
/// Installs a WH_MOUSE_LL hook on a dedicated thread that owns a message loop.
/// Every button event is passed to an <see cref="IMouseEventSink"/>, and the
/// returned verdict decides whether the event reaches the rest of the system.
/// </summary>
public sealed class LowLevelMouseHook : IDisposable
{
    private static readonly double TicksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;

    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly IMouseEventSink _sink;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly long _epochTicks = Stopwatch.GetTimestamp();

    private IntPtr _hookHandle;
    private Thread? _thread;
    private uint _threadId;
    private Exception? _startupError;
    private volatile bool _disposed;

    private long _seenEvents;
    private long _suppressedEvents;

    public LowLevelMouseHook(IMouseEventSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        // The delegate must be kept in a field: if the GC collects it, Windows jumps into freed memory.
        _proc = HookProc;
    }

    /// <summary>Total number of button events the hook has observed.</summary>
    public long SeenEvents => Interlocked.Read(ref _seenEvents);

    /// <summary>Total number of events swallowed before reaching the system.</summary>
    public long SuppressedEvents => Interlocked.Read(ref _suppressedEvents);

    public bool IsRunning => _hookHandle != IntPtr.Zero && !_disposed;

    /// <summary>Microseconds elapsed since the hook was created.</summary>
    public long NowMicroseconds() => (long)((Stopwatch.GetTimestamp() - _epochTicks) * TicksToMicroseconds);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) throw new InvalidOperationException("The hook is already running.");

        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "ChatterFix-Hook",
            // Latency here is click latency, so this thread runs ahead of ordinary work.
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();

        if (!_started.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The hook thread did not start within 5 seconds.");

        if (_startupError is not null)
            throw _startupError;
    }

    private void ThreadMain()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            _hookHandle = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL,
                _proc,
                NativeMethods.GetModuleHandleW(null),
                0);

            if (_hookHandle == IntPtr.Zero)
            {
                _startupError = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetWindowsHookEx failed.");
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
        finally
        {
            _started.Set();
        }

        if (_startupError is not null) return;

        // A low-level hook only fires while its owning thread pumps messages.
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(msg);
            NativeMethods.DispatchMessageW(msg);
        }

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private unsafe IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int message = (int)wParam;

        // Movement and wheel events arrive hundreds of times per second: bail out first.
        if (message is NativeMethods.WM_MOUSEMOVE
            or NativeMethods.WM_MOUSEWHEEL
            or NativeMethods.WM_MOUSEHWHEEL)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var info = (NativeMethods.MSLLHOOKSTRUCT*)lParam;

        if (!TryMapButton(message, info->mouseData, out var button, out var kind))
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        bool injected =
            (info->flags & (NativeMethods.LLMHF_INJECTED | NativeMethods.LLMHF_LOWER_IL_INJECTED)) != 0;

        Interlocked.Increment(ref _seenEvents);

        var mouseEvent = new MouseEvent(button, kind, NowMicroseconds(), injected);

        FilterResult result;
        try
        {
            result = _sink.Handle(in mouseEvent);
        }
        catch
        {
            // If the filter throws, never lose the click: let the event through untouched.
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (result.Action is FilterAction.Suppress or FilterAction.Defer)
        {
            Interlocked.Increment(ref _suppressedEvents);
            return 1; // A non-zero return stops the event from reaching anyone else.
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool TryMapButton(int message, uint mouseData, out MouseButton button, out MouseEventKind kind)
    {
        switch (message)
        {
            case NativeMethods.WM_LBUTTONDOWN:
                button = MouseButton.Left; kind = MouseEventKind.Down; return true;
            case NativeMethods.WM_LBUTTONUP:
                button = MouseButton.Left; kind = MouseEventKind.Up; return true;
            case NativeMethods.WM_RBUTTONDOWN:
                button = MouseButton.Right; kind = MouseEventKind.Down; return true;
            case NativeMethods.WM_RBUTTONUP:
                button = MouseButton.Right; kind = MouseEventKind.Up; return true;
            case NativeMethods.WM_MBUTTONDOWN:
                button = MouseButton.Middle; kind = MouseEventKind.Down; return true;
            case NativeMethods.WM_MBUTTONUP:
                button = MouseButton.Middle; kind = MouseEventKind.Up; return true;

            case NativeMethods.WM_XBUTTONDOWN:
            case NativeMethods.WM_XBUTTONUP:
                uint which = (mouseData >> 16) & 0xFFFF;
                button = which == NativeMethods.XBUTTON2 ? MouseButton.X2 : MouseButton.X1;
                kind = message == NativeMethods.WM_XBUTTONDOWN ? MouseEventKind.Down : MouseEventKind.Up;
                return true;

            default:
                button = default;
                kind = default;
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_threadId != 0)
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        _thread?.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }
}
