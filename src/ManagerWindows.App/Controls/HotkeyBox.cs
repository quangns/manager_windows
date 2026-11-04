using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ManagerWindows.App.Services;

namespace ManagerWindows.App.Controls;

/// <summary>
/// Ô nhập phím tắt: bấm tổ hợp phím (vd. Ctrl+Alt+A) để ghi lại, Backspace/Delete để xoá.
/// Trong lúc ô có focus, phím tắt toàn cục được nhả tạm để tổ hợp đi vào ô thay vì chạy lệnh.
/// Gắn Text với UpdateSourceTrigger=Explicit: chỉ tổ hợp hoàn chỉnh mới được ghi về ViewModel.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ToolTip = Loc.Instance["Hotkey_BoxTip"];
        SetResourceReference(FontFamilyProperty, "MonoFont");
        FontSize = 12.5;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        ToolsService.Current?.Hotkeys.Suspend();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ToolsService.Current?.Hotkeys.Resume();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            SetText("");
            return;
        }
        if (mods == ModifierKeys.None && key is Key.Tab or Key.Escape)
        {
            e.Handled = false; // vẫn chuyển ô / đóng như bình thường
            return;
        }
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var isModifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
        if (!isModifier) parts.Add(HotkeyService.KeyName(key));
        // Chưa có phím chính: chỉ hiện tạm "Ctrl+Alt+…" chờ bấm tiếp.
        if (isModifier) { Text = string.Join("+", parts) + "+…"; return; }
        SetText(string.Join("+", parts));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        // Nhả phím bổ trợ mà chưa có phím chính: trả về giá trị đang lưu.
        if (Text.EndsWith("+…")) Text = CurrentValue;
    }

    private string CurrentValue => (string?)GetValue(SavedProperty) ?? "";

    private static readonly DependencyProperty SavedProperty = DependencyProperty.Register("Saved", typeof(string), typeof(HotkeyBox));

    private void SetText(string value)
    {
        SetValue(SavedProperty, value);
        Text = value;
        GetBindingExpression(TextProperty)?.UpdateSource();
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        if (!Text.EndsWith("+…")) SetValue(SavedProperty, Text);
    }
}
