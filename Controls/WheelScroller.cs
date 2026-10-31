using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Win11Toolbox.Controls;

/// <summary>
/// 修复"鼠标滚轮没反应"：
/// WPF-UI 的 NavigationView 在内容区自带一个 ScrollViewer，页面里再放一个 ScrollViewer 时，
/// 内层会被测量成无限高（ScrollableHeight = 0），但它的 OnMouseWheel 仍会把事件标记为已处理，
/// 结果滚轮被"吃掉"，外层永远不滚。
/// 这里在 Page 的隧道阶段先接管：找到最近的、真正能滚的祖先滚动容器，自己滚，然后标记已处理。
/// </summary>
public static class WheelScroller
{
    private const double LineHeight = 48;

    public static int AttachedCount { get; private set; }
    public static string LastScrolled { get; private set; } = "(未滚动)";
    public static int ScrollCount { get; private set; }

    public static void Attach(FrameworkElement element)
    {
        AttachedCount++;
        element.PreviewMouseWheel += (_, e) =>
        {
            var target = FindScrollable(e.OriginalSource as DependencyObject) ?? FindScrollable(element);
            if (target is null)
            {
                LastScrolled = $"未找到可滚动祖先（{DateTime.Now:HH:mm:ss}）";
                return;
            }

            var delta = -e.Delta / 120.0 * LineHeight;
            target.ScrollToVerticalOffset(target.VerticalOffset + delta);
            e.Handled = true;
            ScrollCount++;
            LastScrolled = $"滚动 {delta:F0}px → Offset={target.VerticalOffset:F0}/{target.ScrollableHeight:F0}";
        };
    }

    /// <summary>从事件源开始往上找第一个"还能滚"的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollable(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is ScrollViewer viewer && viewer.ScrollableHeight > 0)
                return viewer;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
