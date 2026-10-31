using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Win11Toolbox.Pages;
using Win11Toolbox.Services;

namespace Win11Toolbox;

public partial class MainWindow : FluentWindow
{
    private bool _suppressSelection;
    private Type? _currentPage;
    private string _lastPageKey = "home";

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Nav.SelectionChanged += Nav_SelectionChanged;

        // 导航栏的模板套用完成后清一次（窗口 Loaded 里还会再兜一次）
        Nav.Loaded += (_, _) => RemoveScrollFocusVisuals(Nav);

        // 滚轮在窗口级统一接管：覆盖所有页面、下拉弹窗和右键菜单，
        // 不用每个页面自己去挂（漏挂一个就全站滚不动）。
        // 原因：WPF-UI 的 NavigationView 外层自带 ScrollViewer，
        // 会用"无限高度"去测量页面，页面自己的 ScrollViewer 会被量成
        // 和内容一样高（ScrollableHeight=0），滚轮事件被它吃掉后
        // 永远到不了真正能滚的外层。这里在隧道阶段直接把偏移量
        // 写到"最近一个真正能滚的 ScrollViewer"上，并标记已处理。
        Win11Toolbox.Controls.WheelScroller.Attach(this);

        RestoreWindowPlacement();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) => SaveWindowPlacement();
    }

    /// <summary>
    /// 拦掉 <c>WM_ERASEBKGND</c>，这是 WPF 里处理"重绘时闪一下白"的标准做法。
    ///
    /// <para>
    /// 默认流程是：重绘前系统先发 <c>WM_ERASEBKGND</c>，用窗口类的背景刷把整块区域刷一遍，
    /// 然后 WPF 再把内容画上去 —— "先刷白、后绘图"，中间那一帧就是白闪。
    /// WPF 自己会整块重画，根本不需要这一步预擦除，所以直接返回 1 告诉系统"背景我处理了"。
    /// </para>
    /// <para>
    /// 为什么现在才加：切主题时的画面撕裂，主因其实是 <c>HomePage.RunChecks</c> 那次
    /// 4.3 秒的 UI 线程阻塞（已拆成 Collect/Render），阻塞期间窗口没法重画，
    /// 而 DWM 已经把标题栏切成新主题了，于是卡出一帧"新边框 + 旧内容"。
    /// 那个修完之后还剩一个"预擦除"的白闪可能，用这个钩子兜掉。
    /// </para>
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(SuppressEraseBackground);
    }

    private const int WM_ERASEBKGND = 0x0014;

    private static IntPtr SuppressEraseBackground(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_ERASEBKGND) return IntPtr.Zero;

        handled = true;
        return new IntPtr(1);
    }

    // ------------------------------------------------------------------ 窗口位置记忆
    private bool _placementRestored;

    private void RestoreWindowPlacement()
    {
        var cfg = AppConfig.Current;

        Width = cfg.Width;
        Height = cfg.Height;

        if (cfg.Left is double left && cfg.Top is double top && IsOnVisibleScreen(left, top, cfg.Width, cfg.Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (cfg.Maximized)
            WindowState = WindowState.Maximized;

        _placementRestored = true;
    }

    /// <summary>记住的位置可能落在已经拔掉的显示器上，那样窗口会开在屏幕外，必须放弃。</summary>
    private static bool IsOnVisibleScreen(double left, double top, double width, double height)
    {
        try
        {
            var virtualLeft = SystemParameters.VirtualScreenLeft;
            var virtualTop = SystemParameters.VirtualScreenTop;
            var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            // 至少要有 120×80 露在可见区域里，才认为这个位置可用
            return left + 120 > virtualLeft && left + width - 120 < virtualRight
                && top + 80 > virtualTop && top + height - 80 < virtualBottom;
        }
        catch
        {
            return false;
        }
    }

    private void SaveWindowPlacement()
    {
        if (!_placementRestored) return;

        AppConfig.Remember(cfg =>
        {
            cfg.Maximized = WindowState == WindowState.Maximized;
            cfg.LastPage = _lastPageKey;

            // 最大化时 RestoreBounds 才是"还原后"的大小，直接读 Width 会记成全屏
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, ActualWidth, ActualHeight)
                : RestoreBounds;

            if (bounds.Width >= 900 && bounds.Height >= 600)
            {
                cfg.Width = bounds.Width;
                cfg.Height = bounds.Height;
            }

            if (double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
            {
                cfg.Left = bounds.Left;
                cfg.Top = bounds.Top;
            }
        });
    }

    // ------------------------------------------------------------------ 快捷键
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 先判断键，再做视觉树查找 —— 否则每敲一个字都要走一遍树
        bool isF5 = e.Key == System.Windows.Input.Key.F5;
        bool isFind = e.Key == System.Windows.Input.Key.F && IsCtrlDown();
        bool isDigit = e.Key >= System.Windows.Input.Key.D1 && e.Key <= System.Windows.Input.Key.D9 && IsCtrlDown();

        if (!isF5 && !isFind && !isDigit) return;

        if (isDigit)
        {
            NavigateToIndex((int)e.Key - (int)System.Windows.Input.Key.D1);
            e.Handled = true;
            return;
        }

        var page = CurrentPage() as IRefreshablePage;
        if (page == null) return;

        if (isF5)
        {
            page.RefreshPage();
            e.Handled = true;
        }
        else
        {
            page.FocusSearch();
            e.Handled = true;
        }
    }

    private static bool IsCtrlDown() =>
        (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;

    /// <summary>
    /// 当前显示的是哪个页面。NavigationView 没有暴露 Content，
    /// 所以在视觉树里找第一个可见的 Page —— 缓存页虽然还在内存里，但不可见，不会被选中。
    /// </summary>
    private Page? CurrentPage() => FindVisiblePage(Nav);

    private static Page? FindVisiblePage(System.Windows.DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Page page && page.IsVisible)
                return page;

            var found = FindVisiblePage(child);
            if (found != null)
                return found;
        }
        return null;
    }

    /// <summary>Ctrl+1..9：按导航栏顺序跳页。</summary>
    private void NavigateToIndex(int index)
    {
        var items = Nav.MenuItems.OfType<NavigationViewItem>()
            .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>())
            .ToList();

        if (index < 0 || index >= items.Count) return;

        var target = items[index].TargetPageType;
        if (target != null) Navigate(target);
    }

    /// <summary>
    /// 去掉导航区里所有 ScrollViewer 的焦点虚线框。
    /// 左侧那层（PART_ScrollViewer）连焦点都不给：它不可聚焦，点击导航栏空白处
    /// 就不会再冒出一个框住所有导航项的矩形。
    /// </summary>
    private static void RemoveScrollFocusVisuals(System.Windows.DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll)
            {
                scroll.FocusVisualStyle = null;
                if (scroll.Name == "PART_ScrollViewer")
                    scroll.Focusable = false;
            }
            RemoveScrollFocusVisuals(child);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // 模板套好之后再清：导航栏那层 ScrollViewer 点一下空白处就会拿到键盘焦点，
        // WPF 会给它画一个 1px 深色虚线框，把整列导航项框起来 —— 很难看。
        // 这里把 ScrollViewer 的焦点框全关掉，并让左侧那层根本不可聚焦。
        Dispatcher.BeginInvoke(new Action(() => RemoveScrollFocusVisuals(Nav)),
                               System.Windows.Threading.DispatcherPriority.Loaded);

        if (Environment.GetCommandLineArgs().Contains("--bench"))
        {
            RunBench();
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--scrolldebug"))
        {
            Navigate(PageFromArgs() ?? typeof(HomePage));
            await Task.Delay(1200);
            await RunScrollDebugAsync();
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--selftest"))
        {
            await RunSelfTestAsync();
            return;
        }

        // --uistall：量 UI 线程卡顿（可选 --withtaskmgr 自动复现「关任务管理器那一瞬卡」）
        if (Environment.GetCommandLineArgs().Contains("--uistall"))
        {
            Navigate(PageFromArgs() ?? typeof(ToolsPage));
            await Task.Delay(800);
            await RunUiStallAsync();
            return;
        }

        // 无界面自检：设置项能不能探测（托盘模块改用 --probe，不建窗口）
        if (Environment.GetCommandLineArgs().Contains("--easytest"))
        {
            RunEasyTest();
            return;
        }

        // 正常启动：--page 优先，其余一律回主页。
        // 不再恢复"上次停留的页面"——一打开就落在某个工具页反而让人找不到北，
        // 主页才是给人看整体状态的地方。（LastPage 仍写进配置，只作排查用。）
        Navigate(PageFromArgs() ?? typeof(HomePage));
    }

    /// <summary>UI Automation 在 STA 线程上最稳。</summary>
    private static Task<T> RunStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new System.Threading.Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>--easytest：把 40 项设置的探测结果导出来，结果写 easytest.log。</summary>
    private void RunEasyTest()
    {
        var lines = new List<string>();
        try
        {
            var items = Win11Toolbox.SettingsKit.EasySettingsData.Build();
            lines.Add($"设置项：{items.Count}");
            lines.Add("");
            foreach (var it in items)
            {
                string state;
                try { state = it.Probe() ? "已开启" : "默认"; }
                catch (Exception ex) { state = "探测失败:" + ex.Message; }
                lines.Add($"[{it.Category}] {it.Name} = {state}"
                          + (it.NeedAdmin ? "  (需管理员)" : "")
                          + (it.NeedExplorerRestart ? "  (需重启资源管理器)" : ""));
            }
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex);
        }

        try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "easytest.log"), lines); }
        catch { }
        Application.Current.Shutdown(lines.Any(l => l.StartsWith("FAIL")) ? 1 : 0);
    }

    /// <summary>命令行 --page 参数，方便截图和调试。</summary>
    private static Type? PageFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--page");
        if (index < 0 || index + 1 >= args.Length)
            return null;

        return args[index + 1].ToLowerInvariant() switch
        {
            "home" => typeof(HomePage),
            "hardware" => typeof(HardwarePage),
            "fan" => typeof(FanPage),
            "tools" => typeof(ToolsPage),
            "utilities" => typeof(UtilitiesPage),
            "optimize" => typeof(OptimizePage),
            "rightmenu" => typeof(RightMenuPage),
            "tray" => typeof(TrayIconsPage),
            "easysettings" => typeof(EasySettingsPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => null,
        };
    }

    private void Navigate(Type pageType)
    {
        // 设置 IsActive 会再次触发 SelectionChanged，这里要屏蔽回环
        _suppressSelection = true;
        try
        {
            if (_currentPage != pageType)
                Nav.Navigate(pageType);

            foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>()
                              .Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>()))
                item.IsActive = item.TargetPageType == pageType;

            _currentPage = pageType;
            _lastPageKey = AppConfig.KeyOf(pageType);
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (_suppressSelection)
            return;

        if (sender.SelectedItem is not NavigationViewItem item)
            return;

        var target = item.TargetPageType;
        if (target is null || target == _currentPage)
            return;

        _currentPage = target;
        _lastPageKey = AppConfig.KeyOf(target);
        sender.Navigate(target);
    }

    // ------------------------------------------------------------------ 性能 / 滚动诊断
    private void RunBench()
    {
        // 1) 首屏取数（缓存命中应该只有几毫秒）
        var cacheWatch = System.Diagnostics.Stopwatch.StartNew();
        var cached = AppCache.Hardware;
        cacheWatch.Stop();

        // 2) 后台刷新（真正查 WMI）
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _ = SystemInfo.Build();
        watch.Stop();

        var lines = new List<string>
        {
            cached is null
                ? $"缓存：无（需完整采集 {watch.ElapsedMilliseconds} ms）"
                : $"缓存命中：{cacheWatch.ElapsedMilliseconds} ms（采集时间 {cached.CollectedAt:HH:mm:ss}）",
            $"WMI 全量采集：{watch.ElapsedMilliseconds} ms",
        };
        lines.AddRange(SystemInfo.Timings
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{pair.Value,7} ms  {pair.Key}"));
        lines.AddRange(Enumerable.Range(0, 0).Select(_ => ""));

        var path = Path.Combine(AppContext.BaseDirectory, "bench.log");
        File.WriteAllLines(path, lines);
        Application.Current.Shutdown(0);
    }

    /// <summary>列出所有 ScrollViewer 的度量结果，并用真实滚轮消息测试一次。</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public MouseInput mi;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private async Task RunScrollDebugAsync()
    {
        var lines = new List<string>();
        lines.Add($"WheelScroller 已挂页面：{Win11Toolbox.Controls.WheelScroller.AttachedCount}");
        var viewers = new List<System.Windows.Controls.ScrollViewer>();
        if (Content is DependencyObject root)
            Collect(root, viewers);

        foreach (var viewer in viewers)
        {
            var owner = FindOwner(viewer)?.GetType().Name ?? "(无)";
            lines.Add($"{owner,-18} Extent={viewer.ExtentHeight,6:F0} Viewport={viewer.ViewportHeight,5:F0} " +
                      $"Scrollable={viewer.ScrollableHeight,5:F0} Offset={viewer.VerticalOffset,5:F0} " +
                      $"Hook={viewer.IsHitTestVisible}");
        }

        var page = viewers.FirstOrDefault(v => FindOwner(v) is System.Windows.Controls.Page);
        if (page is null)
        {
            lines.Add("页面里没有 ScrollViewer");
        }
        else
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // 命中测试：拿窗口中间内容区的真实元素作为事件源（不依赖窗口焦点）
            var probe = new System.Windows.Point(ActualWidth * 0.55, ActualHeight * 0.55);
            var hit = System.Windows.Media.VisualTreeHelper.HitTest(this, probe);
            var over = hit?.VisualHit;
            lines.Add($"命中元素 = {over?.GetType().Name ?? "null"}");

            if (over is System.Windows.IInputElement source)
            {
                var pageElement = (System.Windows.FrameworkElement)FindOwner(page)?.Content is FrameworkElement c ? c : page;
                // 从页面元素上以 Source=光标下元素 抛隧道事件，等价于真实滚轮从窗口滚到目标元素
                for (var i = 0; i < 3; i++)
                {
                    var args = new System.Windows.Input.MouseWheelEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice,
                        Environment.TickCount,
                        -120)
                    {
                        RoutedEvent = System.Windows.UIElement.PreviewMouseWheelEvent,
                        Source = source,
                    };
                    pageElement.RaiseEvent(args);
                    await Task.Delay(120);
                }

                foreach (var viewer in viewers.Where(v => v.ScrollableHeight > 0))
                    lines.Add($"滚轮后 {FindOwner(viewer)?.GetType().Name ?? "(外层)"} " +
                              $"Offset={viewer.VerticalOffset:F0}/{viewer.ScrollableHeight:F0}");
                lines.Add($"滚轮后 {Win11Toolbox.Controls.WheelScroller.LastScrolled}（WheelScroller 是否接管）");
            }
        }

        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "scrolldebug.log"), lines);
        Application.Current.Shutdown(0);
    }

    private static void Collect(DependencyObject root, List<System.Windows.Controls.ScrollViewer> sink)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer viewer)
                sink.Add(viewer);
            Collect(child, sink);
        }
    }

    private static System.Windows.Controls.Page? FindOwner(DependencyObject element)
    {
        var current = element;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Page page)
                return page;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ------------------------------------------------------------------ 自检
    /// <summary>--selftest：把每个页面都切一遍，确认没有异常，结果写 selftest.log。</summary>
    private async Task RunSelfTestAsync()
    {
        var order = new (string Name, Type Type)[]
        {
            ("home", typeof(HomePage)),
            ("hardware", typeof(HardwarePage)),
            ("fan", typeof(FanPage)),
            ("tools", typeof(ToolsPage)),
            ("utilities", typeof(UtilitiesPage)),
            ("optimize", typeof(OptimizePage)),
            ("rightmenu", typeof(RightMenuPage)),
            ("tray", typeof(TrayIconsPage)),
            ("easysettings", typeof(EasySettingsPage)),
            ("settings", typeof(SettingsPage)),
            ("about", typeof(AboutPage)),
            ("home-again", typeof(HomePage)),
            ("hardware-again", typeof(HardwarePage)),
        };

        var results = new List<string>();
        foreach (var (name, type) in order)
        {
            try
            {
                Navigate(type);
                await Task.Delay(900);
                results.Add($"OK   {name}  ({type.Name})");
            }
            catch (Exception ex)
            {
                results.Add($"FAIL {name}  ({type.Name})  {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 回到主页后等 3 秒，确认监控计时器恢复（离开页面时 Stop，回来必须重新 Start）
        var before = HomePage.SnapshotCount;
        Navigate(typeof(HomePage));
        await Task.Delay(3000);
        var after = HomePage.SnapshotCount;
        results.Add(after > before
            ? $"OK   monitor-resume  ({after - before} 次快照)"
            : "FAIL monitor-resume  回到主页后没有新数据（计时器未恢复）");

        // 工具磁贴的字形：配漏了、或者配了一个超出 BMP 的码位，都不会报错，
        // 只会显示空白/残缺角标，所以自检里主动查一遍
        var missing = Services.ToolCatalog.MissingGlyphs();
        results.Add(missing.Count == 0
            ? "OK   tool-glyphs  (47 个工具字形齐全且都在 BMP 内)"
            : "FAIL tool-glyphs  " + string.Join("、", missing));

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "selftest.log");
            File.WriteAllLines(path, results);
        }
        catch
        {
            // 忽略写文件失败
        }

        Application.Current.Shutdown(results.Any(r => r.StartsWith("FAIL")) ? 1 : 0);
    }

    // ------------------------------------------------------------------ 卡顿探针
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_SETFOCUS = 0x0007;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_PAINT = 0x000F;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

    private static string MsgName(int msg) => msg switch
    {
        WM_ACTIVATE => "WM_ACTIVATE",
        WM_SETFOCUS => "WM_SETFOCUS",
        WM_KILLFOCUS => "WM_KILLFOCUS",
        WM_PAINT => "WM_PAINT",
        WM_NCACTIVATE => "WM_NCACTIVATE",
        WM_WINDOWPOSCHANGED => "WM_WINDOWPOSCHANGED",
        WM_DWMCOMPOSITIONCHANGED => "WM_DWMCOMPOSITIONCHANGED",
        _ => "0x" + msg.ToString("X4"),
    };

    /// <summary>
    /// <c>--uistall [秒数] [txt] [--withtaskmgr]</c>：量 UI 线程到底被谁占住了。
    ///
    /// <para>
    /// 原理：挂一个 8ms 的 <see cref="DispatcherPriority.Send"/> 计时器。它每次触发时算一下
    /// 「距上次触发过了多久」——正常情况下接近 8ms；如果某次隔了 300ms，说明这 300ms 里
    /// UI 线程根本轮不到这个计时器，也就是界面在这段时间是死的（用户看到的就是「卡」）。
    /// </para>
    /// <para>
    /// 只测时长不足以定位。所以同时挂钩子记下 <c>WM_ACTIVATE</c> / <c>WM_PAINT</c> 这些消息的到达时刻，
    /// 两者对在一条时间线上，就能看出「是消息来得晚（UI 线程被占），还是消息来了画得慢（渲染贵）」。
    /// </para>
    /// <para>
    /// <c>--withtaskmgr</c> 会自己开一个任务管理器、等它盖在窗口上、再关掉，把用户描述的场景自动化复现。
    /// </para>
    /// </summary>
    private async Task RunUiStallAsync()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--uistall");

        int seconds = 14;
        string logPath = Path.Combine(AppContext.BaseDirectory, "uistall.log");
        for (int i = index + 1; i < args.Length; i++)
        {
            // 带值的开关，它后面那个词是值、不是位置参数。
            // 踩过一次：--page tools 里的 tools 被当成日志文件名，
            // 而 tools 恰好是项目里的目录 → WriteAllLines 抛异常 → 日志静默丢失。
            if (args[i] is "--page" or "--theme")
            {
                i++;
                continue;
            }

            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            if (int.TryParse(args[i], out var parsed) && parsed > 0) { seconds = parsed; continue; }
            logPath = args[i];
        }

        bool withTaskMgr = args.Contains("--withtaskmgr");
        bool killTaskMgr = args.Contains("--killtaskmgr");
        bool withNotepad = args.Contains("--withnotepad");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<(double At, string Text)>();
        var messages = new List<(double At, string Text)>();
        var gaps = new List<(double At, double Ms)>();
        var gate = new object();

        void Mark(string text)
        {
            lock (gate) marks.Add((clock.Elapsed.TotalMilliseconds, text));
        }

        // 窗口消息时间线：记录到达时刻，用来判断"消息来得晚"还是"画得慢"
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg is WM_ACTIVATE or WM_SETFOCUS or WM_KILLFOCUS or WM_PAINT
                    or WM_NCACTIVATE or WM_WINDOWPOSCHANGED or WM_DWMCOMPOSITIONCHANGED)
                {
                    lock (gate)
                    {
                        // 空闲时 WM_PAINT 极少，但也设个上限防止把内存写爆
                        if (messages.Count < 5000)
                            messages.Add((clock.Elapsed.TotalMilliseconds, MsgName(msg)));
                    }
                }

                return IntPtr.Zero;
            });
        }

        Mark("窗口已就绪，页面=" + (PageFromArgs()?.Name ?? "HomePage"));

        // 复现要忠实：用户是"任务管理器盖在本程序上面再关掉"，
        // 所以本窗口必须先真的在前台，否则关窗口时压根没有 WM_ACTIVATE 回来。
        try
        {
            Activate();
            NativeMethods.BringToFront(new WindowInteropHelper(this).Handle);
            await Task.Delay(400);
        }
        catch
        {
            // 抢不到前台就照常测，只是激活相关的时间线会缺
        }

        Mark("已激活，IsActive=" + IsActive);

        // 整页重绘成本：把当前页面整块软件渲染一遍并计时。
        // 窗口从被遮住到重新露出来，DWM 会要求重画这块区域；
        // 如果这个数很大（几百毫秒），用户就会看到"露出来的时候卡一下"。
        try
        {
            var renderClock = System.Diagnostics.Stopwatch.StartNew();
            var root = (FrameworkElement)Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Max(Width, 1), (int)Math.Max(Height, 1), 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(root);
            renderClock.Stop();
            Mark($"整页重绘成本 {renderClock.Elapsed.TotalMilliseconds:0}ms（{bmp.PixelWidth}x{bmp.PixelHeight}）");
        }
        catch (Exception ex)
        {
            Mark("重绘成本测量失败：" + ex.Message);
        }

        // 8ms 探针：Send 优先级，UI 线程一空闲就立刻跑，能把阻塞时长量到毫秒
        double lastTick = clock.Elapsed.TotalMilliseconds;
        var probe = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(8) };
        probe.Tick += (_, _) =>
        {
            var now = clock.Elapsed.TotalMilliseconds;
            var gap = now - lastTick;
            lastTick = now;
            if (gap >= 40) gaps.Add((now, gap));
        };

        lastTick = clock.Elapsed.TotalMilliseconds;
        probe.Start();
        Mark("探针启动");

        // 第二条探针：后台线程量 DWM 合成耗时。
        // 为什么需要它：上面那条只量 UI 线程。WPF 的绘制分两段——UI 线程算视觉树，
        // 渲染线程光栅化。渲染线程慢、或者整个桌面合成卡住时，UI 线程照样很闲，
        // 但用户看到的窗口就是不动。DwmFlush 变慢 = 卡的是整个桌面，不是本程序。
        var dwmStalls = new List<(double At, double Ms)>();
        var dwmStop = false;
        var dwmThread = new Thread(() =>
        {
            while (!Volatile.Read(ref dwmStop))
            {
                var t0 = clock.Elapsed.TotalMilliseconds;
                NativeMethods.WaitForComposition();
                var t1 = clock.Elapsed.TotalMilliseconds;
                if (t1 - t0 >= 40)
                {
                    lock (gate)
                    {
                        if (dwmStalls.Count < 5000) dwmStalls.Add((t1, t1 - t0));
                    }
                }
            }
        })
        { IsBackground = true, Name = "dwm-probe" };
        dwmThread.Start();

        if (withTaskMgr)
        {
            _ = Task.Run(() => DriveTaskManager(Mark, killTaskMgr));
        }

        if (withNotepad)
        {
            // 对照组：换一个普通程序盖上来再关掉。
            // 如果换记事本就不卡了，说明问题出在任务管理器（它是提权进程，关闭时前台交接慢），
            // 而不是本程序的绘制或线程。
            _ = Task.Run(() => DriveNotepad(Mark));
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds));
        probe.Stop();
        Volatile.Write(ref dwmStop, true);
        Mark("探针停止");

        // ---------------------------------------------------------- 汇总
        var lines = new List<string>
        {
            $"时长 {seconds}s，探针间隔 8ms，阈值 40ms",
            $"窗口位置 {Left:0},{Top:0}  {Width:0}x{Height:0}  激活={IsActive}",
            $"UI 线程卡顿（>40ms）：{gaps.Count}",
            $"桌面合成卡顿（DwmFlush >40ms）：{dwmStalls.Count}",
        };

        // 关键指标：从"发出关闭"到"本窗口重新拿到焦点"隔了多久。
        // 这段时间里用户点什么都没反应 —— 他嘴里的"卡"多半就是它。
        double closeAt = -1, reactivateAt = -1;
        lock (gate)
        {
            var closeMark = marks.FirstOrDefault(m => m.Text.StartsWith("准备关闭", StringComparison.Ordinal));
            if (closeMark.Text is not null) closeAt = closeMark.At;
            if (closeAt >= 0)
            {
                var act = messages.FirstOrDefault(m => m.At > closeAt && m.Text == "WM_ACTIVATE");
                if (act.Text is not null) reactivateAt = act.At;
            }
        }

        if (closeAt >= 0)
        {
            lines.Add(reactivateAt >= 0
                ? $"关闭 → 重新激活：{reactivateAt - closeAt:0}ms（这段时间点窗口没反应）"
                : "关闭 → 重新激活：一直没拿回焦点");
        }

        if (gaps.Count > 0)
        {
            var worst = gaps.OrderByDescending(g => g.Ms).Take(5).ToList();
            lines.Add($"  UI 最长 {worst[0].Ms:0}ms，累计 {gaps.Sum(g => g.Ms) / 1000:0.00}s");
            foreach (var (at, ms) in worst)
                lines.Add($"    t={at:0}ms  阻塞 {ms:0}ms");
        }

        if (dwmStalls.Count > 0)
        {
            var worst = dwmStalls.OrderByDescending(g => g.Ms).Take(5).ToList();
            lines.Add($"  合成最长 {worst[0].Ms:0}ms，累计 {dwmStalls.Sum(g => g.Ms) / 1000:0.00}s");
            foreach (var (at, ms) in worst)
                lines.Add($"    t={at:0}ms  合成 {ms:0}ms");
        }

        lines.Add("");
        lines.Add("==== 时间线（标记 + 窗口消息）====");
        var timeline = new List<(double At, string Text)>();
        lock (gate)
        {
            timeline.AddRange(marks);
            timeline.AddRange(messages);
        }

        foreach (var (at, text) in timeline.OrderBy(t => t.At))
            lines.Add($"  {at,8:0}ms  {text}");

        lines.Add("");
        lines.Add("==== 卡顿明细 ====");
        foreach (var (at, ms) in gaps)
            lines.Add($"  {at,8:0}ms  阻塞 {ms:0}ms");

        try
        {
            File.WriteAllLines(logPath, lines);
        }
        catch (Exception ex)
        {
            // 别再把失败吞掉：先退回程序目录，还不行就报到 stderr
            try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "uistall.log"), lines); } catch { }
            try { Console.Error.WriteLine($"uistall 写 {logPath} 失败：{ex.Message}"); } catch { }
        }

        Application.Current.Shutdown(0);
    }

    /// <summary>对照组：记事本盖上来再关掉，看本程序多久能拿回前台。</summary>
    private static void DriveNotepad(Action<string> mark)
    {
        try
        {
            Thread.Sleep(2500);
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = true,
            });

            if (proc is null) { mark("notepad 启动失败"); return; }
            mark("notepad 启动 pid=" + proc.Id);

            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 120 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                try { proc.Refresh(); hwnd = proc.MainWindowHandle; } catch { break; }
            }

            mark(hwnd == IntPtr.Zero ? "notepad 没出现窗口" : "notepad 窗口就绪");
            if (hwnd != IntPtr.Zero) NativeMethods.BringToFront(hwnd);

            Thread.Sleep(2500);
            mark("准备关闭 notepad");
            bool asked = false;
            try { asked = proc.CloseMainWindow(); } catch { }
            mark("notepad CloseMainWindow=" + asked);

            for (int i = 0; i < 120; i++)
            {
                Thread.Sleep(50);
                try { if (proc.HasExited) { mark("notepad 已退出"); return; } } catch { return; }
            }

            try { proc.Kill(); } catch { }
        }
        catch (Exception ex)
        {
            mark("notepad 流程异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>
    /// 后台线程里驱动任务管理器：开 → 等窗口 → 让它盖在前面 → 关。
    /// 每步都记时间戳，好和 UI 线程的阻塞对上。
    /// <para>
    /// <paramref name="kill"/> 为真时直接 <c>Kill</c> 而不是发 <c>WM_CLOSE</c>：
    /// 这是一个对照组 —— 优雅退出要跑关闭流程（保存设置、注销通知、回收进程句柄），
    /// 强杀没有这些。两组一比，就能分清"卡"是任务管理器退出慢，还是我们被它的关闭流程拖住了。
    /// </para>
    /// </summary>
    private static void DriveTaskManager(Action<string> mark, bool kill)
    {
        try
        {
            Thread.Sleep(2500);

            // 已经在跑就别再开一个（任务管理器是单实例，再开会变成"激活已有窗口"然后立刻退出）
            System.Diagnostics.Process? proc = System.Diagnostics.Process.GetProcessesByName("taskmgr").FirstOrDefault();
            if (proc is null)
            {
                proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe")
                {
                    UseShellExecute = true,
                });
                mark("taskmgr 启动 pid=" + (proc?.Id ?? 0));
            }
            else
            {
                mark("taskmgr 已在运行 pid=" + proc.Id);
            }

            if (proc is null)
            {
                mark("taskmgr 启动失败");
                return;
            }

            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 120 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                try { proc.Refresh(); hwnd = proc.MainWindowHandle; } catch { break; }
            }

            mark(hwnd == IntPtr.Zero ? "taskmgr 没出现窗口" : "taskmgr 窗口就绪 hwnd=" + hwnd);

            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.BringToFront(hwnd);
                mark("已把 taskmgr 切到前台");
            }

            Thread.Sleep(2500);

            mark("准备关闭 taskmgr" + (kill ? "（强杀对照组）" : ""));
            if (kill)
            {
                try { proc.Kill(); } catch { }
            }
            else
            {
                bool asked = false;
                try { asked = proc.CloseMainWindow(); } catch { }
                mark("CloseMainWindow=" + asked);
            }

            for (int i = 0; i < 120; i++)
            {
                Thread.Sleep(50);
                try
                {
                    if (proc.HasExited) { mark("taskmgr 已退出"); return; }
                }
                catch { mark("taskmgr 已退出(取状态异常)"); return; }
            }

            mark("taskmgr 20 秒还没退，强杀");
            try { proc.Kill(); } catch { }
        }
        catch (Exception ex)
        {
            mark("taskmgr 流程异常：" + ex.GetType().Name + " " + ex.Message);
        }
    }
}
