using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace Win11Toolbox.Controls;

/// <summary>
/// 一个很小的单行输入对话框。系统设置里「重命名」弹的就是这种小窗，
/// WPF-UI 没有现成的带输入框的消息框，这里自己拼一个。
/// </summary>
internal static class InputDialog
{
    /// <summary>对话框四周的留白。上边要和下边一样，见 <see cref="BuildContent"/>。</summary>
    internal const double Edge = 24;

    /// <summary>
    /// 拼出对话框的内容区。单独拆出来是为了让诊断（<c>--dialogdump</c>）能量到真实的布局，
    /// 而不是靠看截图猜。
    /// </summary>
    internal static (System.Windows.Controls.StackPanel Panel, TextBox Input, Button Cancel, Button Ok)
        BuildContent(string label, string initial, string hint, string okText)
    {
        var input = new TextBox
        {
            Text = initial ?? "",
            Margin = new Thickness(0, 10, 0, 0),
        };

        var hintBlock = new TextBlock
        {
            Text = hint,
            Margin = new Thickness(0, 8, 0, 0),
            FontTypography = FontTypography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        var cancel = new Button { Content = "取消", MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = okText, MinWidth = 88, Appearance = ControlAppearance.Primary };

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(Edge) };
        panel.Children.Add(new TextBlock { Text = label, FontTypography = FontTypography.Body });
        panel.Children.Add(input);
        panel.Children.Add(hintBlock);
        panel.Children.Add(buttons);

        return (panel, input, cancel, ok);
    }

    public static Task<string?> ShowAsync(Window? owner, string title, string label,
                                          string initial, string hint, string okText = "确定")
    {
        var tcs = new TaskCompletionSource<string?>();
        var (panel, input, cancel, ok) = BuildContent(label, initial, hint, okText);

        var window = new FluentWindow
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            // FluentWindow 的默认样式里写死了 MinHeight=320。这种小输入框的内容通常只要 180 左右，
            // 多出来的 130 多像素全堆在"下一步"下面，看着就是一大块空白。
            // 把它归零，让 SizeToContent 说了算，上下留白才会都是 Edge。
            MinHeight = 0,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = false,
            ExtendsContentIntoTitleBar = true,
            Content = panel,
        };

        void Close(string? result)
        {
            tcs.TrySetResult(result);
            window.Close();
        }

        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) => Close(input.Text.Trim());
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
            else if (e.Key == Key.Enter) Close(input.Text.Trim());
        };
        window.Closed += (_, _) => tcs.TrySetResult(null);

        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        window.ShowDialog();
        return tcs.Task;
    }
}
