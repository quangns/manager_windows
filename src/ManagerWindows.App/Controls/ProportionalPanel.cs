using System.Windows;
using System.Windows.Controls;

namespace ManagerWindows.App.Controls;

/// <summary>
/// Xếp phần tử theo hàng ngang, độ rộng tỉ lệ với <see cref="WeightProperty"/> (sơ đồ phân vùng kiểu Disk Management),
/// nhưng không hẹp hơn <see cref="MinItemWidth"/> để ô nhỏ (EFI, MSR) vẫn bấm được.
/// </summary>
public sealed class ProportionalPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(ProportionalPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetWeight(DependencyObject d) => (double)d.GetValue(WeightProperty);
    public static void SetWeight(DependencyObject d, double value) => d.SetValue(WeightProperty, value);

    public double MinItemWidth { get; set; } = 72;

    private double[] Widths(double available)
    {
        var n = InternalChildren.Count;
        var widths = new double[n];
        if (n == 0) return widths;
        if (double.IsInfinity(available) || available <= MinItemWidth * n)
        {
            Array.Fill(widths, MinItemWidth);
            return widths;
        }
        var weights = InternalChildren.Cast<UIElement>().Select(c => Math.Max(GetWeight(c), 0.0001)).ToArray();
        // Ô quá nhỏ được nâng lên tối thiểu; phần còn lại chia theo tỉ lệ cho các ô khác (lặp tới khi ổn định).
        var fixedSet = new bool[n];
        while (true)
        {
            var freeWidth = available - fixedSet.Count(f => f) * MinItemWidth;
            var freeWeight = weights.Where((_, i) => !fixedSet[i]).Sum();
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                if (fixedSet[i]) { widths[i] = MinItemWidth; continue; }
                widths[i] = freeWidth * weights[i] / freeWeight;
                if (widths[i] < MinItemWidth) { fixedSet[i] = true; changed = true; }
            }
            if (!changed) return widths;
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var widths = Widths(available.Width);
        double height = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Measure(new Size(widths[i], available.Height));
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? widths.Sum() : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var widths = Widths(final.Width);
        double x = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Arrange(new Rect(x, 0, widths[i], final.Height));
            x += widths[i];
        }
        return final;
    }
}
