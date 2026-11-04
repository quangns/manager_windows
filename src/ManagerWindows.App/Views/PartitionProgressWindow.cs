using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ManagerWindows.App.Services;
using ManagerWindows.Core.Partitions;
using ManagerWindows.Platform;

namespace ManagerWindows.App.Views;

/// <summary>
/// Tiến độ thao tác phân vùng dài (dời phân vùng có thể mất hàng giờ): bước, %, tốc độ, thời gian còn lại.
/// "Dừng" chỉ dừng ở ranh giới đoạn đã chép; nhật ký giữ lại để tiếp tục sau.
/// </summary>
public sealed class PartitionProgressWindow : Window
{
    private readonly TextBlock _step = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 8, Margin = new Thickness(0, 12, 0, 8) };
    private readonly TextBlock _detail = new() { FontSize = 12 };
    private readonly Button _stop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private (long Done, double Seconds) _rateMark;
    private double _rate;
    private bool _finished;

    public CancellationToken Token => _cts.Token;

    public PartitionProgressWindow(string title)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        SetResourceReference(BackgroundProperty, "Canvas");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;
        _bar.SetResourceReference(StyleProperty, "Meter");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        var warn = new TextBlock { Text = Loc.Instance["Part_ProgressWarn"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
        warn.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        _stop = new Button { Content = Loc.Instance["Part_Pause"], HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _stop.SetResourceReference(StyleProperty, "Secondary");
        _stop.Click += (_, _) => Pause();
        Content = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Children = { _step, _bar, _detail, warn, _stop } };
        _step.Text = Loc.Instance["Part_Preparing"];
    }

    private void Pause()
    {
        if (_cts.IsCancellationRequested) return;
        if (MessageBox.Show(this, Loc.Instance["Part_PauseAsk"], Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cts.Cancel();
        _stop.IsEnabled = false;
        _step.Text = Loc.Instance["Part_Pausing"];
    }

    public void Report(PlanProgress p)
    {
        var name = Loc.Instance["Part_Step_" + p.Kind];
        _step.Text = Loc.F("Part_StepOf", ("step", p.Step), ("steps", p.Steps), ("name", name));
        var fraction = p.Total > 0 ? (double)p.Done / p.Total : 0;
        _bar.Value = fraction;
        if (p.Kind != StepKind.Move || p.Total <= 1)
        {
            _bar.IsIndeterminate = p.Done < p.Total;
            _detail.Text = Loc.Instance["Part_Working"];
            return;
        }
        _bar.IsIndeterminate = false;
        // Tốc độ trung bình trượt, cập nhật mỗi ~2 giây.
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _rateMark.Seconds >= 2)
        {
            var r = (p.Done - _rateMark.Done) / Math.Max(0.001, now - _rateMark.Seconds);
            _rate = _rate <= 0 ? r : _rate * 0.7 + r * 0.3;
            _rateMark = (p.Done, now);
        }
        var eta = _rate > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / _rate) : (TimeSpan?)null;
        _detail.Text = Loc.F("Part_MoveDetail",
            ("pct", (fraction * 100).ToString("0.0")),
            ("done", Fmt.Bytes(p.Done)), ("total", Fmt.Bytes(p.Total)),
            ("speed", _rate > 0 ? Fmt.Bytes((long)_rate) + "/s" : "…"),
            ("eta", eta is { } e ? Fmt.Duration(Math.Max(60, e.TotalSeconds)) : "…"));
    }

    /// <summary>Thao tác xong (thành công hay không): cho phép đóng.</summary>
    public void Finish()
    {
        _finished = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_finished) return;
        // Bấm X giữa chừng = dừng an toàn, không đóng ngay.
        e.Cancel = true;
        Pause();
    }
}
