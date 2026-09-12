namespace ChatterFix.Core;

/// <summary>
/// Receives every mouse button event the hook layer observes.
/// WARNING: this runs inside the low-level hook callback, on the critical path.
/// It must not allocate, wait on a lock, or write to a file or the console.
/// Windows silently disables the hook if the callback takes longer than 300 ms.
/// </summary>
public interface IMouseEventSink
{
    FilterResult Handle(in MouseEvent e);
}
