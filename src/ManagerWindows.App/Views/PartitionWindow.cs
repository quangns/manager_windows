using System.Windows;
using ManagerWindows.App.Services;
using ManagerWindows.App.ViewModels;

namespace ManagerWindows.App.Views;

/// <summary>Cửa sổ quản lý phân vùng chạy quyền Administrator (app khởi động với --partitions).</summary>
public sealed class PartitionWindow : Window
{
    public PartitionWindow()
    {
        Title = $"{Loc.Instance["Tab_Partitions"]} – Manager Windows (Administrator)";
        Width = 1100;
        Height = 780;
        MinWidth = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        SetResourceReference(BackgroundProperty, "Canvas");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;
        var vm = new PartitionsViewModel();
        Content = new PartitionsView { DataContext = vm };
        Loaded += async (_, _) => await vm.RefreshAsync();
    }
}
