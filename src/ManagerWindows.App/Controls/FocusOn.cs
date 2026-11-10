using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ManagerWindows.App.Controls;

/// <summary>
/// <c>c:FocusOn.Request="{Binding X}"</c>: mỗi lần X đổi sang true thì đặt con trỏ nhập vào ô này (và chọn hết chữ trong TextBox).
/// ViewModel đặt lại false sau đó để lần sau còn kích hoạt được.
/// </summary>
public static class FocusOn
{
    public static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached(
        "Request", typeof(bool), typeof(FocusOn), new PropertyMetadata(false, (d, e) =>
        {
            if (e.NewValue is not true || d is not UIElement el) return;
            el.Dispatcher.BeginInvoke(() =>
            {
                el.Focus();
                Keyboard.Focus(el);
                if (el is TextBox t) t.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }));

    public static bool GetRequest(DependencyObject d) => (bool)d.GetValue(RequestProperty);
    public static void SetRequest(DependencyObject d, bool v) => d.SetValue(RequestProperty, v);
}
