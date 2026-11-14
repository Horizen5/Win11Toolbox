using System;
using System.Threading;

namespace Win11Toolbox.Services;

/// <summary>
/// 内存回收：把托管堆收干净，再把「已提交但当前没被访问」的物理页还给系统。
///
/// <para>
/// 背景：WPF 程序任务管理器里看到的「内存」是<b>工作集</b>，里面包含大量
/// 只在启动/构造页面时碰过一次、之后再也用不到的东西 —— JIT 产生的代码页、
/// BAML 解析出的模板、一次性 WMI 查询的缓冲、字体文件的映射页。
/// 这些东西不占托管堆（实测托管堆只有 12 MB，工作集却有 80~115 MB），
/// 所以 <c>GC.Collect</c> 一个人收不掉，必须再调 <c>SetProcessWorkingSetSize</c>
/// 把驻留页换出去（页面内容还在页面文件里，下次访问靠缺页中断拿回来，不影响正确性）。
/// </para>
///
/// <para>
/// <b>为什么要挑时机：</b>裁剪之后的第一次重绘要把页重新读回内存，
/// 窗口正被用户操作时做这件事会造成一次 100 ms 级别的卡顿。
/// 所以只在「用户看不到这个窗口」的时候做：窗口最小化、切到别的程序（比如
/// 切去看任务管理器）、以及启动后静置 8 秒。前台交互期间一律不动。
/// </para>
/// </summary>
public static class MemoryTrimmer
{
    private static int _busy;
    private static long _lastTrim;

    /// <summary>两次裁剪之间的最小间隔（毫秒），避免来回切窗口时反复抖动。</summary>
    private const int MinIntervalMs = 3000;

    /// <summary>
    /// 收一遍托管堆 + 裁剪工作集。可以随便调，内部自己防重入、限频率。
    /// </summary>
    /// <param name="force">true 时忽略最小间隔（启动后那一次用它）。</param>
    public static void Trim(bool force = false)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            LastResult = "正在回收，跳过";
            return;
        }

        try
        {
            long now = EnvironmentCompat.TickCount64;
            long last = Interlocked.Read(ref _lastTrim);
            if (!force && last != 0 && now - last < MinIntervalMs)
            {
                LastResult = "距上次不足 " + MinIntervalMs + " ms，跳过";
                return;
            }
            Interlocked.Exchange(ref _lastTrim, now);

            // 先两轮 GC：第一轮收垃圾并执行终结器，第二轮收掉终结器释放出来的对象。
            // 只有 10 来 MB 的堆，UI 线程上跑一轮实测 2~8 ms。
            LOHCompactionHint();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            // 再把物理页还给系统。minSize/maxSize 都传 -1 = 「尽量回收」。
            bool ok = NativeMethods.TrimWorkingSet();
            LastResult = ok ? "已回收" : "裁剪失败，错误码 " + NativeMethods.LastTrimError;
        }
        catch (Exception ex)
        {
            // 回收失败不影响功能，下次再说（--memcheck 里能看到是被哪一步挡下的）
            LastResult = "异常：" + ex.GetType().Name + " " + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>最近一次回收的结果，排查用（<c>--memcheck</c> 会打印）。</summary>
    public static string LastResult { get; private set; } = "未执行";

    /// <summary>
    /// 启动后静置一段时间做一次裁剪：首次进主页后 WPF 已经把模板、字体、图标
    /// 全部摸过一遍，这些页此时都在物理内存里躺着，之后不会再以同样方式用到。
    ///
    /// <para>
    /// 只在窗口<b>不是活动窗口</b>时动手（<paramref name="isBusy"/> 返回 true 就往后顺延）。
    /// 窗口在前台、用户正在滚动的时候裁剪，下一次重绘要把几十 MB 的页重新缺页读回来，
    /// 会看见一次明显的停顿 —— 省内存不能拿流畅度换。
    /// </para>
    /// </summary>
    public static void TrimWhenIdle(TimeSpan delay, Func<bool> isBusy, int maxAttempts = 4)
    {
        int attempts = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = delay,
        };
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (isBusy() && attempts < maxAttempts)
                return;                     // 用户在用，下一轮再看

            timer.Stop();
            Trim(force: true);
        };
        timer.Start();
    }

    /// <summary>
    /// 大对象堆碎片整理：只有 85 KB 以上的对象才进 LOH，本程序里主要是
    /// 画面缓冲和一次性读进来的大数组，量大时顺手压一下。失败无所谓。
    /// </summary>
    private static void LOHCompactionHint()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        }
        catch
        {
        }
    }
}
