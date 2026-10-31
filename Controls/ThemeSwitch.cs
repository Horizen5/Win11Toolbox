using System;
using System.Diagnostics;

namespace Win11Toolbox.Controls;

/// <summary>
/// 主题切换的唯一入口：执行一次切换，并记录耗时。
///
/// <para>
/// <b>这里故意什么都不盖。</b> 这一路走过两次弯路，都记在这里，别再犯：
/// </para>
/// <list type="number">
/// <item>
/// <b>淡出（错）</b>：切换前抓一张亮色快照盖上去，切完再淡出。
/// 亮暗互相渐变必然穿过中灰 —— <c>0.7×(250,250,250) + 0.3×(32,40,50) = (185,187,190)</c>，
/// 实测截图里的窗口底色正是 <c>(185,188,192)</c>。整屏"发灰发脏"的一帧。
/// </item>
/// <item>
/// <b>硬切（也错）</b>：蒙层全程不透明，切完换成新快照再一次性摘掉。
/// 灰是没了，但不透明蒙层会把 <c>ToggleSwitch</c> 正在滑的滑块盖住 ——
/// 滑块动画 167ms（`--toggleanim` 从模板里读出来的），盖 100ms 再揭开，
/// 滑块已经在终点，用户看到的就是"顿一下再直接跳过去"。
/// </item>
/// </list>
///
/// <para>
/// <b>真正让画面撕裂的不是 DWM。</b> 翻 WPF-UI 源码可以看到
/// <c>WindowBackgroundManager.UpdateBackground</c> 确实是
/// <c>RemoveBackdrop</c> → <c>RemoveBackground</c> → <c>ApplyBackdrop</c> 拆了重建，
/// 但三个都是同步 Win32 调用，微秒级完成，DWM 不会在中间合成一帧。
/// 那次"新边框 + 旧内容"的撕裂帧，是 <c>HomePage.RunChecks</c> 阻塞 UI 线程 <b>4.3 秒</b>造成的 ——
/// 阻塞期间窗口没法重画，而标题栏已经被 DWM 切成新主题了。拆成 Collect/Render 两段之后就没这个问题了。
/// </para>
///
/// <para>
/// 剩下的"预擦除白闪"由 <c>MainWindow</c> 里拦截 <c>WM_ERASEBKGND</c> 兜掉，这是 WPF 的标准做法。
/// </para>
/// </summary>
internal static class ThemeSwitch
{
    /// <summary>最近一次执行的说明，给 <c>--themeflash</c> 诊断看。</summary>
    internal static string LastReport = "（还没跑过）";

    /// <summary>执行一次主题切换。耗时超过这个值就说明有人在切换路径上做了重活。</summary>
    internal const long BudgetMs = 60;

    public static void Run(Action change)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            change();
        }
        finally
        {
            long ms = watch.ElapsedMilliseconds;
            LastReport = ms <= BudgetMs
                ? $"改主题 {ms}ms（预算 {BudgetMs}ms 内）"
                : $"改主题 {ms}ms —— 超预算 {BudgetMs}ms，有人在切换路径上做了重活，查 UiTheme.HandlerLog";
        }
    }
}
