using System.Runtime.InteropServices;

namespace ChatterFix.Core.Native;

/// <summary>
/// Sends synthetic button events through SendInput. Used to release a button
/// whose release we held back, and by the self-test to prove the hook is live.
/// Injected events carry a marker so the hook recognises them and leaves them alone.
/// </summary>
public static class InputInjector
{
    public static bool SendButton(MouseButton button, MouseEventKind kind)
    {
        if (!TryGetFlags(button, kind, out uint flags, out uint mouseData))
            return false;

        var inputs = new NativeMethods.INPUT[1];
        inputs[0].type = NativeMethods.INPUT_MOUSE;
        inputs[0].u.mi = new NativeMethods.MOUSEINPUT
        {
            dx = 0,
            dy = 0,
            mouseData = mouseData,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = new UIntPtr(NativeMethods.ChatterFixSignature),
        };

        uint sent = NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == 1;
    }

    private static bool TryGetFlags(MouseButton button, MouseEventKind kind, out uint flags, out uint mouseData)
    {
        bool down = kind == MouseEventKind.Down;
        mouseData = 0;

        switch (button)
        {
            case MouseButton.Left:
                flags = down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP;
                return true;
            case MouseButton.Right:
                flags = down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP;
                return true;
            case MouseButton.Middle:
                flags = down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP;
                return true;
            case MouseButton.X1:
                flags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
                mouseData = NativeMethods.XBUTTON1;
                return true;
            case MouseButton.X2:
                flags = down ? NativeMethods.MOUSEEVENTF_XDOWN : NativeMethods.MOUSEEVENTF_XUP;
                mouseData = NativeMethods.XBUTTON2;
                return true;
            default:
                flags = 0;
                return false;
        }
    }
}
