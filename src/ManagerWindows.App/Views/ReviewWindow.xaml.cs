using System.Windows;

namespace ManagerWindows.App.Views;

public partial class ReviewWindow : Window
{
    public ReviewWindow() => InitializeComponent();

    private void Done_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
