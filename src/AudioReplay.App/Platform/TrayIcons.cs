using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AudioReplay.App.Platform;

/// <summary>Builds the tray icon from the embedded .ico; while recording a red dot is drawn over it.</summary>
internal static class TrayIcons
{
    public static Icon Create(bool recording)
    {
        var size = SystemInformation.SmallIconSize;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AudioReplay.ico")
            ?? throw new InvalidOperationException("Embedded icon is missing.");
        using var baseIcon = new Icon(stream, size);
        if (!recording) return (Icon)baseIcon.Clone();

        using var bitmap = baseIcon.ToBitmap();
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var d = Math.Max(6, size.Width * 5 / 10);
            var rect = new Rectangle(bitmap.Width - d, bitmap.Height - d, d - 1, d - 1);
            using var outline = new SolidBrush(Color.FromArgb(230, 20, 20, 20));
            g.FillEllipse(outline, Rectangle.Inflate(rect, 1, 1));
            using var dot = new SolidBrush(Color.FromArgb(255, 235, 50, 50));
            g.FillEllipse(dot, rect);
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
