using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Win11Toolbox.Controls;

/// <summary>
/// 把承载页面的外层 ScrollViewer「锁住」，让页面拿到确定的高度和宽度。
///
/// 背景：WPF-UI 的 NavigationView 在内容外面套了一层 ScrollViewer。
/// 那一层会用「无限高」去测量页面，于是页面里 ListBox 永远量不到边界，
/// 虚拟化失效、整页变成一个超长滚动条，标题还会被挤出可视区切掉一条。
///
/// 列表页自己就是满屏布局、内部列表单独滚动，根本不需要外层再滚。
/// 所以进入页面时把外层滚动关掉（内容按可视区大小测量，页面就撑满了），
/// 离开时恢复原样，不影响主页 / 硬件信息这类需要整页滚动的页面。
/// </summary>
internal static class ViewportLock
{
    private sealed class Original
    {
        public ScrollBarVisibility V;
        public ScrollBarVisibility H;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, Original> Saved = new();

    public static void Lock(FrameworkElement page)
    {
        var sv = FindAncestor<ScrollViewer>(page);
        if (sv == null) return;

        if (!Saved.TryGetValue(sv, out var original))
        {
            original = new Original { V = sv.VerticalScrollBarVisibility, H = sv.HorizontalScrollBarVisibility };
            Saved.Add(sv, original);
        }

        sv.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        sv.ScrollToVerticalOffset(0);
    }

    public static void Unlock(FrameworkElement page)
    {
        var sv = FindAncestor<ScrollViewer>(page);
        if (sv == null || !Saved.TryGetValue(sv, out var original)) return;

        sv.VerticalScrollBarVisibility = original.V;
        sv.HorizontalScrollBarVisibility = original.H;
        Saved.Remove(sv);
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var cur = start;
        while (cur != null)
        {
            cur = VisualTreeHelper.GetParent(cur);
            if (cur is T hit) return hit;
        }
        return null;
    }
}
