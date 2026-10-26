using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace AudioDesk;

public static class PopupPlacement
{
    /// <summary>Opens a popup below its button with their right edges lined up, so it grows toward the window's inside.
    /// The 8px nudge cancels the margin that leaves room for the popup's shadow.</summary>
    public static readonly CustomPopupPlacementCallback BelowRightAligned = (popupSize, targetSize, _) =>
    [
        new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 8, targetSize.Height - 4), PopupPrimaryAxis.Horizontal),
    ];
}

/// <summary>Empty text → Visible, otherwise Collapsed. Shows a text box's placeholder only while it's empty.</summary>
public sealed class EmptyToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true → false. Used to stop a toggle button reopening its popup on the click that closed it.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
