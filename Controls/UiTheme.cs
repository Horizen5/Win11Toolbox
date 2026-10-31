using System;
using System.Windows;
using System.Windows.Media;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using Wpf.Ui.Controls;

namespace Win11Toolbox.Controls;

/// <summary>统一的取色 / 取字体工具，全部走 WPF-UI 的主题资源，深浅色自动切换。</summary>
public static class UiTheme
{
    public static Brush Brush(string key, Brush fallback)
        => Application.Current.TryFindResource(key) as Brush ?? fallback;

    public static Brush CardBackground => Brush("CardBackgroundFillColorDefaultBrush", Brushes.White);
    public static Brush CardStroke => Brush("CardStrokeColorDefaultBrush", new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)));
    public static Brush TextPrimary => Brush("TextFillColorPrimaryBrush", Brushes.Black);
    public static Brush TextSecondary => Brush("TextFillColorSecondaryBrush", new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)));

    public static Brush Accent => Brush("SystemAccentColorPrimaryBrush",
        Brush("SystemAccentColorBrush", new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4))));

    // ---- 状态语义色 ----
    // 千万不要写死 #0F7B0F 这种"浅色底上的深绿"：深色模式下它压在 #202020 上几乎看不见。
    // Win11 的语义色在深浅两套主题里是不同的值（深色下是 #6CCB5F 这种亮色），
    // 所以统一走资源键，由主题决定具体颜色。
    // 注意这些是属性而不是静态字段：静态字段会在类型初始化时把当时的主题色"焊死"，
    // 用户切主题后就不会变了。TryFindResource 返回的是主题字典里的同一个实例，
    // 反复读不会产生新对象，绑定也不会误判成"值变了"。
    public static Brush Success => Brush("SystemFillColorSuccessBrush", new SolidColorBrush(Color.FromRgb(0x0F, 0x7B, 0x0F)));
    public static Brush Caution => Brush("SystemFillColorCautionBrush", new SolidColorBrush(Color.FromRgb(0x9D, 0x5D, 0x00)));
    public static Brush Critical => Brush("SystemFillColorCriticalBrush", new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)));
    public static Brush Attention => Brush("SystemFillColorAttentionBrush", new SolidColorBrush(Color.FromRgb(0x00, 0x5F, 0xB8)));
    public static Brush Neutral => Brush("TextFillColorTertiaryBrush", new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

    /// <summary>
    /// 主题切换后触发。
    /// 原因：列表行的状态色是"算出来的属性"（<c>Foreground="{Binding StateBrush}"</c>），
    /// 绑定只在 PropertyChanged 时才重读；用户切成深色后，行对象不会自己知道，
    /// 那个深绿/深红的状态点还是旧主题的颜色。切主题时喊一嗓子，让各页把列表重绑一遍。
    ///
    /// <para>
    /// <b>订阅者必须是"只重画、不重读"</b>。换颜色是纯视觉的事，一旦顺手去重新枚举
    /// 注册表 / 托盘 / powercfg，切一次主题就会卡好几秒 —— 实测曾经有 4.3 秒。
    /// 用 <see cref="Time"/> 包一下就能看出是谁在拖后腿。
    /// </para>
    /// </summary>
    public static event Action? ThemeChanged;

    /// <summary>各订阅者最近一次耗时，格式 <c>名字=毫秒</c>，给 --themeflash 看。</summary>
    internal static string HandlerLog = "";

    /// <summary>包一个订阅者并计时，结果记进 <see cref="HandlerLog"/>。</summary>
    internal static void Time(string name, Action action)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { action(); }
        finally { HandlerLog += $"{name}={watch.ElapsedMilliseconds}ms "; }
    }

    public static void NotifyThemeChanged()
    {
        HandlerLog = "";
        ThemeChanged?.Invoke();
    }

    public static TextBlock Title(string text) => new() { Text = text, FontTypography = FontTypography.Title };
    public static TextBlock Subtitle(string text) => new() { Text = text, FontTypography = FontTypography.Subtitle };
    public static TextBlock Body(string text) => new() { Text = text, FontTypography = FontTypography.Body };
    public static TextBlock BodyStrong(string text) => new() { Text = text, FontTypography = FontTypography.BodyStrong };

    public static TextBlock Secondary(string text)
    {
        var block = Body(text);
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    public static TextBlock Caption(string text, bool secondary = true)
    {
        var block = new TextBlock { Text = text, FontTypography = FontTypography.Caption };
        if (secondary)
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }
}
