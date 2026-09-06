using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ConnectionChecker;

/// <summary>
/// Draws the cyan-ring app icon at runtime (no .ico asset) for both the
/// window/taskbar icon and the tray icon.
/// </summary>
public static class AppIcon
{
    /// <summary>32x32 for title bar and taskbar.</summary>
    public static readonly ImageSource WindowIcon = CreateImageSource(32);

    /// <summary>16x16 GDI icon for the notification-area tray icon; red ring when alerting.</summary>
    public static Drawing.Icon CreateTrayIcon(bool alert = false)
        => CreateGdiIcon(16, alert ? Drawing.Color.FromArgb(255, 59, 92) : null);

    private static ImageSource CreateImageSource(int size)
    {
        using var icon = CreateGdiIcon(size);
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Drawing.Icon CreateGdiIcon(int size, Drawing.Color? ringColor = null)
    {
        using var bmp = new Drawing.Bitmap(size, size);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.FromArgb(10, 14, 23));
            float pen = size / 8f;
            float inset = pen * 1.5f;
            using var ring = new Drawing.Pen(ringColor ?? Drawing.Color.FromArgb(0, 229, 255), pen);
            g.DrawEllipse(ring, inset, inset, size - 2 * inset, size - 2 * inset);
        }

        // GetHicon hands us an HICON that Icon.FromHandle does NOT own; clone
        // to an owned Icon, then release the native handle ourselves.
        var hIcon = bmp.GetHicon();
        try
        {
            using var unowned = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)unowned.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
