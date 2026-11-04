using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ManagerWindows.App.Services;
using ManagerWindows.App.ViewModels;
using ManagerWindows.Core.Partitions;

namespace ManagerWindows.App.Views;

/// <summary>
/// Hộp thoại nhập cho thao tác phân vùng: kích thước (GB, có giới hạn), lựa chọn, văn bản; thao tác xoá dữ liệu
/// bắt gõ đúng chữ xác nhận mới bấm được nút đồng ý.
/// </summary>
public sealed class PartitionDialog : Window
{
    private readonly StackPanel _body = new();
    private readonly Button _ok;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly List<Func<string?>> _validators = new();
    private string? _confirmWord;
    private TextBox? _confirmBox;

    public PartitionDialog(string title, string message, bool danger = false)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        ShowInTaskbar = Owner == null;
        SetResourceReference(BackgroundProperty, "Canvas");
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;

        var head = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        if (danger) head.SetResourceReference(TextBlock.ForegroundProperty, "Critical");
        _body.Children.Add(head);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Critical");

        _ok = new Button { Content = Loc.Instance[danger ? "Part_DoDanger" : "Part_DoIt"], IsDefault = true };
        _ok.SetResourceReference(StyleProperty, "Primary");
        if (danger)
        {
            _ok.SetResourceReference(BackgroundProperty, "Critical");
            _ok.SetResourceReference(BorderBrushProperty, "Critical");
        }
        _ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = Loc.Instance["Btn_Cancel"], IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "Secondary");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(_ok);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(_body);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 10, 0, 4) };
        _body.Children.Add(t);
        return t;
    }

    public void AddNote(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _body.Children.Add(t);
    }

    /// <summary>Ô kích thước theo GB, kèm nút "Nhỏ nhất"/"Lớn nhất" và kiểm tra giới hạn.</summary>
    public SizeField AddSize(string label, ulong current, ulong min, ulong max)
    {
        Label(label);
        var box = new TextBox { Text = Gb(current), Width = 140 };
        var unit = new TextBlock { Text = "GB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
        var minBtn = new Button { Content = Loc.Instance["Part_Min"], Margin = new Thickness(0, 0, 6, 0) };
        var maxBtn = new Button { Content = Loc.Instance["Part_Max"], Margin = new Thickness(0) };
        minBtn.SetResourceReference(StyleProperty, "Link");
        maxBtn.SetResourceReference(StyleProperty, "Link");
        minBtn.Click += (_, _) => box.Text = Gb(min);
        maxBtn.Click += (_, _) => box.Text = Gb(max);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(unit);
        row.Children.Add(minBtn);
        row.Children.Add(maxBtn);
        _body.Children.Add(row);
        var hint = new TextBlock { Text = Loc.F("Part_SizeRange", ("current", Fmt.Bytes((long)current)), ("min", Fmt.Bytes((long)min)), ("max", Fmt.Bytes((long)max))), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "InkMuted");
        _body.Children.Add(hint);

        var field = new SizeField(box);
        _validators.Add(() =>
        {
            // Ô cho phép 0 (vd. "vùng trống phía trước" = 0): ParseSize coi 0 là không hợp lệ nên xử lý riêng.
            var v = DiskLayout.ParseSize(box.Text)
                    ?? (min == 0 && double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var z) && z == 0 ? 0UL : null);
            if (v == null) return Loc.Instance["Part_BadSize"];
            // Ô hiển thị làm tròn 2 chữ số: chấp nhận lệch nhỏ quanh giới hạn rồi kẹp lại.
            const ulong slack = 16 * DiskLayout.MiB;
            if (v < (min > slack ? min - slack : 0) || v > max + slack)
                return Loc.F("Part_SizeOutOfRange", ("min", Fmt.Bytes((long)min)), ("max", Fmt.Bytes((long)max)));
            field.Value = Math.Clamp(v.Value, min, max) / DiskLayout.MiB * DiskLayout.MiB;
            return null;
        });
        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return field;
    }

    private static string Gb(ulong bytes) => (bytes / (double)DiskLayout.GiB).ToString("0.##", CultureInfo.InvariantCulture);

    public ChoiceField AddChoice(string label, IEnumerable<Option<string>> options, string selected)
    {
        Label(label);
        var list = options.ToList();
        var combo = new ComboBox { ItemsSource = list, DisplayMemberPath = nameof(Option<string>.Label), SelectedValuePath = nameof(Option<string>.Value), Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        combo.SelectedValue = list.Any(o => o.Value == selected) ? selected : list.FirstOrDefault()?.Value;
        _body.Children.Add(combo);
        return new ChoiceField(combo);
    }

    public TextBox AddText(string label, string value)
    {
        Label(label);
        var box = new TextBox { Text = value, MaxLength = 32 };
        _body.Children.Add(box);
        return box;
    }

    /// <summary>Kiểm tra thêm khi bấm đồng ý (trả thông báo lỗi hoặc null).</summary>
    public void AddValidator(Func<string?> validate) => _validators.Add(validate);

    /// <summary>Ô tick bắt buộc (vd. "Tôi đã sao lưu dữ liệu"): chưa tick thì không bấm được đồng ý.</summary>
    public void RequireCheck(string text)
    {
        var box = new CheckBox { Content = text, Margin = new Thickness(0, 14, 0, 0) };
        box.SetResourceReference(ForegroundProperty, "Ink");
        _body.Children.Add(box);
        _checks.Add(box);
        box.Checked += (_, _) => UpdateOk();
        box.Unchecked += (_, _) => UpdateOk();
        UpdateOk();
    }

    private readonly List<CheckBox> _checks = new();

    private void UpdateOk() =>
        _ok.IsEnabled = _checks.All(c => c.IsChecked == true) && (_confirmBox == null || Matches(_confirmBox.Text));

    /// <summary>Phải gõ đúng chữ này (không phân biệt hoa thường, có/không dấu) mới bấm được nút đồng ý.</summary>
    public void RequireTyping(string word)
    {
        _confirmWord = word;
        Label(Loc.F("Part_TypeToConfirm", ("word", word)));
        _confirmBox = new TextBox();
        _confirmBox.TextChanged += (_, _) => UpdateOk();
        _body.Children.Add(_confirmBox);
        UpdateOk();
        Loaded += (_, _) => _confirmBox.Focus();
    }

    private bool Matches(string text) =>
        _confirmWord != null && string.Equals(Strip(text.Trim()), Strip(_confirmWord), StringComparison.OrdinalIgnoreCase);

    private static string Strip(string s) =>
        new(s.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => c is 'đ' or 'Đ' ? 'd' : c).ToArray());

    private void Accept()
    {
        if (!_ok.IsEnabled) return;
        foreach (var v in _validators)
            if (v() is { } err)
            {
                _error.Text = err;
                _error.Visibility = Visibility.Visible;
                return;
            }
        DialogResult = true;
    }

    public sealed class SizeField(TextBox box)
    {
        public TextBox Box { get; } = box;
        /// <summary>Kích thước đã kiểm tra (byte, căn 1 MB); có sau khi bấm đồng ý.</summary>
        public ulong? Value { get; internal set; }
    }

    public sealed class ChoiceField
    {
        private readonly ComboBox _combo;

        public ChoiceField(ComboBox combo)
        {
            _combo = combo;
            combo.SelectionChanged += (_, _) => Changed?.Invoke();
        }

        public string? Value => _combo.SelectedValue as string;
        public event Action? Changed;
    }
}
