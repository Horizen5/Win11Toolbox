using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace Win11Toolbox.Services;

/// <summary>
/// 启动耗时追踪。默认<b>完全不工作</b>（只多一次静态布尔判断），
/// 只有设了环境变量 <c>W11TB_TRACE=1</c> 才往 <c>%TEMP%\w11tb-startup.log</c> 里按毫秒记时间线。
///
/// <para>
/// 为什么要专门搞一个：启动卡顿靠读代码猜不出来。窗口 700ms 就出来了，然后整块界面
/// 冻住三秒多，这期间既没有异常也没有日志 —— 只能把"谁在什么时候占着 UI 线程"逐点量出来。
/// 日志里同时有一条 8ms 的探针，UI 线程一被堵住就会留下一个 ≥100ms 的空档，
/// 空档的<b>结束时刻</b>就是阻塞代码交还控制权的那一刻，正好对上前一个 Mark。
/// </para>
/// </summary>
internal static class StartupTrace
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("W11TB_TRACE") == "1";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), "w11tb-startup.log");

    private static readonly object Gate = new();
    private static bool _ready;
    private static double _lastTick;

    public static bool IsEnabled => Enabled;

    public static double Now => Clock.Elapsed.TotalMilliseconds;

    /// <summary>记一个时间点。失败一律吞掉 —— 追踪代码自己绝不能影响被测程序。</summary>
    public static void Mark(string text)
    {
        if (!Enabled) return;

        try
        {
            lock (Gate)
            {
                if (!_ready)
                {
                    _ready = true;
                    File.WriteAllText(LogPath,
                        "Win11工具箱 启动时间线  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine,
                        new UTF8Encoding(false));
                }

                File.AppendAllText(LogPath,
                    $"  +{Clock.Elapsed.TotalMilliseconds,8:0} ms  {text}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>记一段的耗时，用法 <c>using (StartupTrace.Scope("x")) { … }</c>。</summary>
    public static IDisposable Scope(string text) => new ScopeImpl(text);

    private sealed class ScopeImpl : IDisposable
    {
        private readonly string _text;
        private readonly double _start;

        public ScopeImpl(string text)
        {
            _text = text;
            _start = Now;
        }

        public void Dispose()
        {
            if (!Enabled) return;
            Mark($"{_text} 用时 {Now - _start:0} ms");
        }
    }

    /// <summary>
    /// 启动一条 8ms 的 UI 线程探针（Send 优先级 = UI 线程一空闲就跑）。
    /// 两次 tick 之间隔了多久，就是 UI 线程被占了多久。
    /// </summary>
    public static void StartStallProbe(Dispatcher dispatcher, double thresholdMs = 100)
    {
        if (!Enabled) return;

        _lastTick = Clock.Elapsed.TotalMilliseconds;

        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(8),
        };

        timer.Tick += (_, _) =>
        {
            double now = Clock.Elapsed.TotalMilliseconds;
            double gap = now - _lastTick;
            _lastTick = now;

            if (gap >= thresholdMs)
                Mark($"⚠ UI 线程被占用 {gap:0} ms（结束于此刻）");
        };

        timer.Start();
        Mark("探针启动（8ms / 阈值 " + thresholdMs.ToString("0") + "ms）");
    }
}
