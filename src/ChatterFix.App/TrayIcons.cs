using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ChatterFix.App;

/// <summary>
/// Draws the tray icon at runtime instead of shipping image files, so the icon
/// scales to whatever DPI the notification area is using and its colour can carry
/// state: green while protecting, grey while paused.
/// </summary>
internal static class TrayIcons
{
    private static readonly Color ActiveColour = Color.FromArgb(46, 160, 67);
    private static readonly Color PausedColour = Color.FromArgb(110, 118, 129);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(bool active, int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float margin = size * 0.12f;
            float width = size - margin * 2;
            float height = size - margin * 1.4f;

            var body = new RectangleF(margin, margin * 0.6f, width, height);
            var colour = active ? ActiveColour : PausedColour;

            using var fill = new SolidBrush(colour);
            using var outline = new Pen(Color.FromArgb(230, 255, 255, 255), size * 0.07f);

            // Mouse body.
            g.FillEllipse(fill, body);
            g.DrawEllipse(outline, body);

            // The split between the two buttons, so the shape reads as a mouse.
            float centreX = size / 2f;
            g.DrawLine(outline, centreX, body.Top + size * 0.04f, centreX, body.Top + height * 0.38f);
            g.DrawLine(outline, body.Left + size * 0.04f, body.Top + height * 0.38f,
                                body.Right - size * 0.04f, body.Top + height * 0.38f);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives destroying the temporary GDI handle.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
