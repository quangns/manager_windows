using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ManagerMac.Views;

public partial class ReviewWindow : Window
{
    public ReviewWindow() => InitializeComponent();

    private void Done_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
