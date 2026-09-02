using System.Windows;
using Gaska.Payments.Desktop.ViewModels;

namespace Gaska.Payments.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnHideToast(object sender, RoutedEventArgs e) =>
        (DataContext as MainViewModel)?.HideToast();
}
