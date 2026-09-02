using System.Windows;

namespace Gaska.Payments.Desktop.Mvvm;

/// <summary>
/// A proxy that gives elements outside the visual tree access to the view model.
/// </summary>
/// <remarks>
/// <c>DataGrid</c> columns are not part of the visual tree, so they do not inherit
/// <c>DataContext</c> and an ordinary binding in a header finds nothing. <c>Freezable</c> is the
/// exception here: WPF passes it the data context of whoever owns the resource.
/// </remarks>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
