using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace AudioDesk;

internal static class IconHelper
{
    private static readonly Dictionary<string, ImageSource?> AppIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app icon (Assets\AudioDesk.ico) at the tray size for the current DPI.</summary>
    public static Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AudioDesk.ico"))
            ?? throw new InvalidOperationException("App icon resource is missing.");
        using var stream = resource.Stream;
        return new Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    public static ImageSource ToImageSource(Drawing.Icon icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    public static ImageSource? ForExecutable(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (AppIcons.TryGetValue(path, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            using var icon = Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null) image = ToImageSource(icon);
        }
        catch { }

        AppIcons[path] = image;
        return image;
    }
}
