using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ManagerWindows.App.Services;

namespace ManagerWindows.App.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    protected AppHost Host => AppHost.Current;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";

    /// <summary>Chạy tác vụ nặng ở luồng nền, hiện trạng thái bận và báo lỗi thân thiện.</summary>
    protected async Task RunBusyAsync(Func<Task> work)
    {
        if (Busy) return;
        Busy = true;
        Status = Loc.Instance["Busy"];
        try { await work(); }
        catch (Exception ex)
        {
            Log.Error(GetType().Name, ex);
            Status = Loc.F("Error_Generic", ("error", ex.Message));
        }
        finally { Busy = false; }
    }

    protected static bool Confirm(string message) =>
        MessageBox.Show(message, Loc.Instance["App_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    protected static void Info(string message) =>
        MessageBox.Show(message, Loc.Instance["App_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
}

/// <summary>Lựa chọn cho ComboBox, nhãn lấy theo ngôn ngữ hiện tại.</summary>
public sealed record Option<T>(T Value, string Label);

public static class TextList
{
    public static string Join(IEnumerable<string> items) => string.Join(Environment.NewLine, items);

    public static List<string> Split(string text) =>
        text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
