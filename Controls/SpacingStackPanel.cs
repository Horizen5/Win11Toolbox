using System.Windows;
using System.Windows.Controls;

namespace Win11Toolbox.Controls;

/// <summary>WPF 的 StackPanel 没有 Spacing，这里补一个（只处理垂直方向）。</summary>
public class SpacingStackPanel : StackPanel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(SpacingStackPanel),
        new PropertyMetadata(0d, (d, _) => ((SpacingStackPanel)d).ApplySpacing()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
    {
        base.OnVisualChildrenChanged(added, removed);
        ApplySpacing();
    }

    private void ApplySpacing()
    {
        for (var i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not FrameworkElement element)
                continue;

            var last = i == Children.Count - 1;
            element.Margin = new Thickness(0, 0, 0, last ? 0 : Spacing);
        }
    }
}
