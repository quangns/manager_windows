using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ManagerWindows.App.Controls;
using ManagerWindows.App.Services;

namespace ManagerWindows.App.Views;

/// <summary>Chỉnh ảnh vừa chụp: khoanh vùng, mũi tên, tô sáng, che kín, đánh số; rồi sao chép / lưu.</summary>
public partial class CaptureEditorWindow : Window
{
    /// <summary>Bảng màu: đỏ, cam, vàng, xanh lá, xanh dương, tím, đen, trắng.</summary>
    private static readonly string[] Palette = ["#E53935", "#FB8C00", "#FDD835", "#43A047", "#1E88E5", "#8E24AA", "#000000", "#FFFFFF"];

    private string? _savedPath;
    private bool _dirty;

    public CaptureEditorWindow(BitmapSource shot)
    {
        InitializeComponent();
        Board.SetSource(shot);
        Board.Changed += () => _dirty = true;
        SizeText.Text = $"{shot.PixelWidth} × {shot.PixelHeight}";

        var opt = AppHost.Current.Config.Tools.Capture;
        foreach (var hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "color",
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = hex,
                IsChecked = hex.Equals(opt.Color, StringComparison.OrdinalIgnoreCase),
            };
            rb.Checked += (_, _) => SetColor(color, hex);
            Swatches.Children.Add(rb);
        }
        if (Swatches.Children.OfType<RadioButton>().All(r => r.IsChecked != true))
            ((RadioButton)Swatches.Children[0]).IsChecked = true;
        else
            Board.Color = (Color)ColorConverter.ConvertFromString(opt.Color);

        ThicknessBox.ItemsSource = new[] { Loc.Instance["Cap_Thin"], Loc.Instance["Cap_Medium"], Loc.Instance["Cap_Thick"] };
        ThicknessBox.SelectedIndex = Math.Clamp(opt.Thickness, 1, 3) - 1;

        // Cửa sổ vừa khít ảnh (tối đa 85% màn hình), ảnh nhỏ thì cửa sổ nhỏ theo.
        var area = SystemParameters.WorkArea;
        Width = Math.Clamp(shot.PixelWidth / DpiScale() + 80, MinWidth, area.Width * 0.85);
        Height = Math.Clamp(shot.PixelHeight / DpiScale() + 170, MinHeight, area.Height * 0.85);
        Loaded += (_, _) => { Activate(); Board.Focus(); };
    }

    private double DpiScale() => VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this).DpiScaleX;

    private void SetColor(Color color, string hex)
    {
        Board.Color = color;
        AppHost.Current.Config.Tools.Capture.Color = hex;
    }

    private void OnTool(object sender, RoutedEventArgs e)
    {
        if (Board == null) return;
        Board.Tool = sender switch
        {
            _ when sender == ToolEllipse => AnnotationTool.Ellipse,
            _ when sender == ToolArrow => AnnotationTool.Arrow,
            _ when sender == ToolHighlight => AnnotationTool.Highlight,
            _ when sender == ToolRedact => AnnotationTool.Redact,
            _ when sender == ToolNumber => AnnotationTool.Number,
            _ => AnnotationTool.Rectangle,
        };
        Board.Cursor = Board.Tool == AnnotationTool.Number ? Cursors.Hand : Cursors.Cross;
    }

    private void OnThickness(object sender, SelectionChangedEventArgs e)
    {
        var level = ThicknessBox.SelectedIndex + 1;
        Board.ThicknessLevel = level;
        AppHost.Current.Config.Tools.Capture.Thickness = level;
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Board.Undo();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Board.Export() is not { } img) return;
        if (CaptureOutput.Copy(img))
        {
            AppHost.Current.SaveConfig();
            _dirty = false;
            ToastWindow.Show(Loc.Instance["Cap_Copied"]);
            Close();
        }
        else StatusText.Text = Loc.Instance["Cap_CopyFailed"];
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save(CaptureOutput.Folder());

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = CaptureOutput.FileName(),
            DefaultExt = ".png",
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
            InitialDirectory = CaptureOutput.Folder(),
        };
        if (dlg.ShowDialog(this) == true) Save(null, dlg.FileName);
    }

    private void Save(string? folder, string? path = null)
    {
        if (Board.Export() is not { } img) return;
        try
        {
            _savedPath = CaptureOutput.Save(img, path ?? Path.Combine(folder!, CaptureOutput.FileName()));
            _dirty = false;
            AppHost.Current.SaveConfig();
            StatusText.Text = Loc.F("Cap_Saved", ("path", _savedPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = Loc.F("Error_Generic", ("error", ex.Message));
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) Close();
        else if (ctrl && e.Key == Key.Z) Board.Undo();
        else if (ctrl && e.Key == Key.C) OnCopy(this, e);
        else if (ctrl && e.Key == Key.S) OnSave(this, e);
        else if (!ctrl && e.Key is >= Key.D1 and <= Key.D6 && e.OriginalSource is not TextBox)
        {
            ToggleButton[] tools = [ToolRect, ToolEllipse, ToolArrow, ToolHighlight, ToolRedact, ToolNumber];
            tools[e.Key - Key.D1].IsChecked = true;
        }
        else return;
        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        // Có chú thích chưa sao chép/lưu: hỏi trước khi đóng.
        if (_dirty && Board.HasAnnotations &&
            MessageBox.Show(this, Loc.Instance["Cap_DiscardAsk"], Loc.Instance["App_Title"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
        else
            AppHost.Current.SaveConfig();
    }
}
