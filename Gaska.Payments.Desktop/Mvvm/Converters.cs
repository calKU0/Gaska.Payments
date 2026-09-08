using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows;

namespace Gaska.Payments.Desktop.Mvvm;

/// <summary>True shows the element, false collapses it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Empty text collapses the element.
/// </summary>
/// <remarks>
/// Used on the register icon frame: a register we have no flag for - the social fund, the
/// auxiliary accounts - was left with an empty rounded box, which reads as a defect rather than
/// as the absence of an icon.
/// </remarks>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Nothing chosen shows the element - the caption a drop-down carries while it is empty.
/// </summary>
/// <remarks>
/// Bound rather than left to a template trigger. A trigger comparing the combo box's selection
/// with null fired even on a resolved selection, so the caption sat over the chosen entry; a
/// binding says plainly what is being asked.
/// </remarks>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Green when it balances, red when it does not - used on the amount difference.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush Positive { get; set; } = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));

    public Brush Negative { get; set; } = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Positive : Negative;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
