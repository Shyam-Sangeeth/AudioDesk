using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudioDesk;

/// <summary>
/// Small on-screen confirmation for shortcuts ("Chat volume 65%"). It never takes focus and clicks
/// pass through it, so it's safe to show over a game.
/// </summary>
internal sealed class OsdWindow : Window
{
    private readonly TextBlock _glyph;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly DispatcherTimer _hideTimer;

    public OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var white = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
        var grey = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));

        _glyph = new TextBlock
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 22,
            Foreground = white,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        _title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = white };
        _detail = new TextBlock { FontSize = 13, Foreground = grey, Margin = new Thickness(0, 2, 0, 0), MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_detail);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_glyph);
        row.Children.Add(text);

        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 12, 24, 12),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = row,
        };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
    }

    public void ShowMessage(string glyph, string title, string detail)
    {
        _glyph.Text = glyph;
        _title.Text = title;
        _detail.Text = detail;
        _detail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 56;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        const int GwlExStyle = -20;
        const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
