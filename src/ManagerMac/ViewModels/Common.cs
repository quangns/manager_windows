using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ManagerMac.Services;

namespace ManagerMac.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    protected static MacHost Host => MacHost.Current;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";

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

    protected static Task<bool> Confirm(string message) => Dialogs.AskAsync(message, yesNo: true);

    protected static Task Info(string message) => Dialogs.AskAsync(message, yesNo: false);
}

/// <summary>Mục có nhãn cho ComboBox (XAML không khai báo được kiểu generic Option&lt;T&gt;).</summary>
public interface ILabeled
{
    string Label { get; }
}

public sealed record Option<T>(T Value, string Label) : ILabeled;

public static class TextList
{
    public static string Join(IEnumerable<string> items) => string.Join(Environment.NewLine, items);

    public static List<string> Split(string text) =>
        text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Hộp thoại xác nhận/thông báo đơn giản (Avalonia không có MessageBox sẵn).</summary>
public static class Dialogs
{
    public static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static async Task<bool> AskAsync(string message, bool yesNo)
    {
        var result = false;
        var window = new Window
        {
            Title = Loc.Instance["App_Title"],
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var ok = new Button { Content = Loc.Instance[yesNo ? "Btn_Yes" : "Btn_OK"], Classes = { "primary" }, IsDefault = true };
        var cancel = new Button { Content = Loc.Instance["Review_Cancel"], Classes = { "secondary" }, IsCancel = true, IsVisible = yesNo };
        ok.Click += (_, _) => { result = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        if (Owner is { IsVisible: true } owner) await window.ShowDialog(owner);
        else
        {
            var done = new TaskCompletionSource();
            window.Closed += (_, _) => done.TrySetResult();
            window.Show();
            await done.Task;
        }
        return result;
    }
}
