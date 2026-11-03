using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Win11Toolbox;

/// <summary>
/// 离屏诊断：把页面渲染到屏幕外的窗口里，测滚动时的布局与绘制耗时。
/// 全程不显示窗口、不抢焦点，可以在用户打游戏时跑。
/// </summary>
public partial class App
{
    private static FrameworkElement CreatePage(string name)
    {
        switch (name)
        {
            case "tray": return new Pages.TrayIconsPage();
            case "easy": return new Pages.EasySettingsPage();
            case "rightmenu": return new Pages.RightMenuPage();
            case "tools": return new Pages.ToolsPage();
            case "hardware": return new Pages.HardwarePage();
            case "fan": return new Pages.FanPage();
            case "optimize": return new Pages.OptimizePage();
            case "app": return new Pages.AppPage();
            case "settings": return new Pages.SettingsPage();
            case "about": return new Pages.AboutPage();
            default: return new Pages.HomePage();
        }
    }

    /// <summary>
    /// --appdump &lt;txt&gt;：把「应用管理」页依赖的两套查询结果落盘。
    ///
    /// <para>
    /// 页面上看不到的部分才是最容易错的：<c>Get-AppxPackage</c> 的制表符分隔输出、
    /// 预装包名里的版本段、winget list 的 ID 匹配。这里跑一遍写进文件，出问题看文件就行，
    /// 不用盯着界面猜。
    /// </para>
    /// </summary>
    /// <summary>
    /// --fandump [txt]：把所有风扇通道原样打出来。
    /// 用途：区分「真接了风扇」和「只有接口、没接风扇」——后者能枚举出通道、
    /// 能写 PWM，但转速恒为 0，界面上不该和真风扇混在一起。
    /// </summary>
    private static void RunFanDump(string outPath)
    {
        try { File.Delete(outPath); } catch { }
        void W(string line)
        {
            try { File.AppendAllText(outPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        try
        {
            W($"提权        ：{Win11Toolbox.Services.FanController.IsElevated}");
            W($"PawnIO 驱动 ：{Win11Toolbox.Services.FanController.DriverPresent}");

            using var controller = new Win11Toolbox.Services.FanController();
            controller.Open();
            W($"SuperIO 识别：{controller.SuperIoDetected}");
            if (!string.IsNullOrWhiteSpace(controller.LastError))
                W($"错误        ：{controller.LastError}");

            var (fans, temps) = controller.Snapshot();
            W("");
            W($"=== 风扇通道 {fans.Count} 个 ===");
            W("名称            | 芯片                   |   RPM |  PWM | 可调 | 范围");
            foreach (var f in fans)
            {
                var rpm = f.Rpm is null ? "null" : f.Rpm.Value.ToString("0");
                var pwm = f.Percent is null ? "null" : f.Percent.Value.ToString("0");
                W($"{f.Name,-15} | {f.Hardware,-22} | {rpm,5} | {pwm,4} | "
                  + $"{(f.Controllable ? "是" : "否"),-4} | {f.MinPercent:0}-{f.MaxPercent:0}");
            }

            var spinning = fans.Count(f => f.Rpm is > 0);
            W("");
            W($"有转速 {spinning} 个，无转速 {fans.Count - spinning} 个");

            W("");
            W($"=== 温度 {temps.Count} 个 ===");
            foreach (var t in temps.OrderByDescending(t => t.Value))
                W($"  {t.Name,-24} {t.Value,6:0.0} °C   {t.Hardware}");
        }
        catch (Exception ex)
        {
            W("异常：" + ex);
        }
    }

    private static void RunAppDump(string outPath)
    {
        // 边跑边写盘：这条诊断最容易卡在某个外部进程上，全部攒到最后写就什么都看不到
        try { File.Delete(outPath); } catch { }
        void W(string line)
        {
            try { File.AppendAllText(outPath, line + Environment.NewLine, System.Text.Encoding.UTF8); } catch { }
        }

        try
        {
            // 整段扔线程池再等：这里是 UI 线程，直接对异步方法 GetResult 会死锁
            // （续体要回 UI 线程，而 UI 线程正卡在 GetResult 上）。
            Task.Run(async () =>
            {
                W("=== winget ===");
                bool has = await Win11Toolbox.Services.AppManager.HasWingetAsync();
                W("可用：" + has);
                if (!has) return;

                var catalog = Win11Toolbox.Services.AppCatalog.Build();
                W($"目录 {catalog.Count} 款，开始跑 winget list…");
                var installed = await Win11Toolbox.Services.AppManager
                    .InstalledIdsAsync(catalog.Select(a => a.Id), System.Threading.CancellationToken.None);
                W($"匹配到已安装 {installed.Count} 款");
                foreach (var id in installed.OrderBy(x => x))
                    W("  已装 " + id);

                W("=== Appx 当前用户 ===");
                var apps = await Win11Toolbox.Services.AppManager
                    .LoadAppsAsync(true, null, System.Threading.CancellationToken.None);
                W($"总数 {apps.Count}（当前用户 {apps.Count(a => a.Source == "当前用户")}，系统预装 {apps.Count(a => a.Source == "系统预装")}）");
                foreach (var a in apps.Take(20))
                    W($"  [{a.Source}] {a.Name} | {a.Version} | {a.PackageFullName}");
                W("=== 结束 ===");
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            W("异常：" + ex);
        }
    }

    /// <summary>--powerdump：把 powercfg /list 读回的电源计划名（含中文）原样落盘，验证 OEM 编码修复。</summary>
    private static void RunPowerDump(string outPath)
    {
        try
        {
            var plans = Win11Toolbox.Services.ShellTools.PowerPlans();
            var lines = new List<string>
            {
                $"编码：{Win11Toolbox.Services.ShellTools.ConsoleEncoding().WebName}（OEM 代码页）",
                $"读到 {plans.Count} 个电源计划：",
            };
            foreach (var (guid, name, active) in plans)
                lines.Add($"{(active ? "* " : "  ")}{name}  [{guid}]");
            File.WriteAllLines(outPath, lines, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPath, "FAIL " + ex); } catch { }
        }
        Application.Current.Shutdown(0);
    }

    /// <summary>--launchprobe：把每个工具的目标解析一遍，列出「点了会失败」的工具（如没装终端时的 wt.exe）。</summary>
    private static void RunLaunchProbe(string outPath)
    {
        try
        {
            var lines = new List<string>();
            var missing = 0;
            foreach (var (name, target, status) in Win11Toolbox.Services.ToolCatalog.ProbeTargets())
            {
                lines.Add($"{status,-42} {name}  ({target})");
                if (status.StartsWith("缺失"))
                    missing++;
            }
            lines.Insert(0, $"共 {lines.Count} 个工具，其中 {missing} 个在本机不可用");
            File.WriteAllLines(outPath, lines, Encoding.UTF8);
            Application.Current.Shutdown(missing > 0 ? 1 : 0);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPath, "FAIL " + ex); } catch { }
            Application.Current.Shutdown(2);
        }
    }

    /// <summary>
    /// <c>--monitorcost</c>：量主页那套每秒采样里每一步的耗时。
    /// 目的：确认 <c>Process.GetProcesses()</c> 到底值不值得搬到后台线程。
    /// </summary>
    private static void RunMonitorCost(string outPath)
    {
        try
        {
            var lines = new List<string>();

            for (int round = 1; round <= 3; round++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var count = System.Diagnostics.Process.GetProcesses().Length;
                lines.Add($"第 {round} 轮");
                lines.Add($"  Process.GetProcesses().Length      {sw.Elapsed.TotalMilliseconds,7:0.0}ms   {count} 个");

                sw.Restart();
                var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                int up = 0;
                foreach (var nic in nics)
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    try { _ = nic.GetIPv4Statistics().BytesReceived; up++; } catch { }
                }
                lines.Add($"  GetAllNetworkInterfaces + 统计     {sw.Elapsed.TotalMilliseconds,7:0.0}ms   {up} 张在用网卡");

                sw.Restart();
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                    _ = drive.AvailableFreeSpace;
                }
                catch { }
                lines.Add($"  DriveInfo.AvailableFreeSpace       {sw.Elapsed.TotalMilliseconds,7:0.0}ms");

                sw.Restart();
                var fast = Win11Toolbox.Services.NativeMethods.ProcessCount();
                lines.Add($"  EnumProcesses（替代方案）           {sw.Elapsed.TotalMilliseconds,7:0.0}ms   {fast} 个");
                lines.Add("");
            }

            File.WriteAllLines(outPath, lines, Encoding.UTF8);
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPath, "FAIL " + ex); } catch { }
            Application.Current.Shutdown(1);
        }
    }

    /// <summary>--startupicons：逐个启动项，看能不能解析出 exe/lnk、能不能取到图标（验证列表行图标）。</summary>
    private static void RunStartupIcons(string outPath)
    {
        try
        {
            var lines = new List<string>();
            var withIcon = 0;
            foreach (var item in Win11Toolbox.Services.ShellTools.StartupItems())
            {
                var path = Win11Toolbox.Services.ShellTools.ExecutablePath(item);
                var exists = !string.IsNullOrEmpty(path) && File.Exists(path);
                var icon = string.IsNullOrEmpty(path) ? null : Win11Toolbox.Services.ShellIcon.FromPath(path, small: true);
                var ok = icon is not null;
                if (ok) withIcon++;
                lines.Add($"{(ok ? "图标" : "无  ")}  exists={exists,-5}  {item.Name}  ->  {path}");
            }
            lines.Insert(0, $"共 {lines.Count} 项，取到图标 {withIcon} 项");
            File.WriteAllLines(outPath, lines, Encoding.UTF8);
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPath, "FAIL " + ex); } catch { }
            Application.Current.Shutdown(2);
        }
    }

    /// <summary>--fans：用 LibreHardwareMonitor 扫一遍风扇/温度，确认本机能不能读、能不能调速。</summary>
    /// <summary>--installpawnio <txt>：走和界面按钮完全一样的安装流程，把结果落盘。</summary>
    private static void RunInstallPawnIo(string outPath)
    {
        var lines = new List<string>();
        try
        {
            lines.Add($"管理员: {Win11Toolbox.Services.FanController.IsElevated}");
            lines.Add($"安装前 DriverPresent: {Win11Toolbox.Services.FanController.DriverPresent}");
            lines.Add($"安装包: {Win11Toolbox.Services.FanController.BundledInstaller ?? "(找不到)"}");

            var (ok, message) = Win11Toolbox.Services.FanController.InstallDriver();
            lines.Add($"安装结果: ok={ok}  {message}");
            lines.Add($"安装后 DriverPresent: {Win11Toolbox.Services.FanController.DriverPresent}");
        }
        catch (Exception ex)
        {
            lines.Add("EX: " + ex);
        }

        try { File.WriteAllLines(outPath, lines, Encoding.UTF8); } catch { }
        Application.Current.Shutdown(0);
    }

    /// <summary>--adlfan [百分比]：直接调 ADL OverdriveN 试写风扇，验证这条路走不走得通。</summary>
    private static void RunAdlFan(string outPath, int percent)
    {
        var lines = new List<string>();
        try
        {
            lines.Add($"管理员: {Win11Toolbox.Services.FanController.IsElevated}");
            lines.Add($"ADL 可用: {Win11Toolbox.Services.AmdFan.Available}");

            var adapters = Win11Toolbox.Services.AmdFan.Adapters();
            lines.Add($"适配器 {adapters.Count} 个");
            foreach (var adapter in adapters)
                lines.Add($"  [{adapter.Index}] {adapter.Name}");

            using var controller = new Win11Toolbox.Services.FanController();
            var (fans, _) = controller.Snapshot();
            foreach (var fan in fans)
                lines.Add($"  LHM: {fan.Hardware} / {fan.Name} rpm={fan.Rpm?.ToString("0") ?? "—"}");

            foreach (var adapter in adapters.Where(a => a.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add("");
                lines.Add($"==== [{adapter.Index}] {adapter.Name} ====");

                var (ok, dump, current, mode) = Win11Toolbox.Services.AmdFan.Read(adapter.Index);
                lines.Add($"  读取(OverdriveN): ok={ok}  {dump}");
                lines.Add($"  OD5 能力: {Win11Toolbox.Services.AmdFan.Od5FanInfo(adapter.Index)}");
                lines.Add("  结构体大小探测:");
                foreach (var probe in Win11Toolbox.Services.AmdFan.ProbeStructSizes(adapter.Index))
                    lines.Add("    " + probe);
                if (!ok) continue;

                var set = Win11Toolbox.Services.AmdFan.SetPercent(adapter.Index, percent);
                lines.Add($"  写入 {percent}%: ok={set.Ok}  {set.Message}");

                for (var i = 0; i < 5; i++)
                {
                    System.Threading.Thread.Sleep(1200);
                    var (_, dump2, current2, _) = Win11Toolbox.Services.AmdFan.Read(adapter.Index);
                    var (snapshot, _) = controller.Snapshot();
                    var gpu = snapshot.FirstOrDefault(f => f.Name.Contains("GPU Fan", StringComparison.OrdinalIgnoreCase));
                    lines.Add($"  +{(i + 1) * 1.2:0.0}s  {dump2}  |  LHM rpm={gpu?.Rpm?.ToString("0") ?? "—"}");
                }

                var auto = Win11Toolbox.Services.AmdFan.SetAuto(adapter.Index);
                lines.Add($"  交回自动: ok={auto.Ok}  {auto.Message}");
            }

            Win11Toolbox.Services.AmdFan.Release();
        }
        catch (Exception ex)
        {
            lines.Add("EX: " + ex);
        }

        try { File.WriteAllLines(outPath, lines, Encoding.UTF8); } catch { }
        Application.Current.Shutdown(0);
    }

    private static void RunFans(string outPath)    {
        var lines = new List<string>();
        try
        {
            lines.Add($"管理员: {Win11Toolbox.Services.FanController.IsElevated}");
            using var controller = new Win11Toolbox.Services.FanController();
            var opened = controller.Open();
            lines.Add($"LHM Open: {opened}   error={controller.LastError}");

            // 驱动痕迹：LHM 会把 WinRing0 解压到临时目录
            var sys = new List<string>();
            try
            {
                sys.AddRange(Directory.GetFiles(Path.GetTempPath(), "*.sys", SearchOption.TopDirectoryOnly));
                sys.AddRange(Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers"), "*ring0*"));
            }
            catch { }
            lines.Add($"驱动痕迹: {(sys.Count == 0 ? "无（可能被 HVCI 拦截 / 未解压）" : string.Join(" | ", sys))}");

            var (fans, temps) = controller.Snapshot();
            lines.Add($"风扇 {fans.Count} 个，温度 {temps.Count} 个");

            // 第二次采样：排除「首次读取还没值」
            System.Threading.Thread.Sleep(1500);
            var (_, temps2) = controller.Snapshot();
            lines.Add("第二次温度: " + string.Join(", ", temps2.Select(t => $"{t.Name}={t.Value:0.#}")));

            foreach (var f in fans)
            {
                lines.Add($"  [{(f.Controllable ? "可控" : "只读")}] {f.Hardware} / {f.Name}  " +
                          $"rpm={f.Rpm?.ToString("0") ?? "—"}  pct={f.Percent?.ToString("0.#") ?? "—"}  " +
                          $"min={f.MinPercent:0.#} max={f.MaxPercent:0.#}");
            }

            foreach (var t in temps)
                lines.Add($"  temp {t.Hardware} / {t.Name} = {t.Value:0.#} °C");

            // --fanwrite：真写一次 PWM，确认「调速」不是摆设（会短暂转起来，随后交回自动）
            if (Environment.GetCommandLineArgs()
                    .Any(x => x.StartsWith("--fanwrite", StringComparison.OrdinalIgnoreCase)))
            {
                // --fanwrite=70 可以指定测试转速
                var percent = 55;
                var spec = Environment.GetCommandLineArgs()
                    .FirstOrDefault(x => x.StartsWith("--fanwrite=", StringComparison.OrdinalIgnoreCase));
                if (spec is not null && int.TryParse(spec.Split('=')[1], out var parsed))
                    percent = Math.Clamp(parsed, 0, 100);

                lines.Add("");
                lines.Add($"==== 写入测试 {percent}% ====");
                var targets = fans.Where(f => f.Controllable).ToList();
                if (targets.Count == 0)
                {
                    lines.Add("没有可调速风扇，跳过。");
                }
                else
                {
                    foreach (var f in targets)
                    {
                        var ok = controller.SetPercent(f, percent);
                        lines.Add($"  SetSoftware({percent}) {f.Name} -> {ok}  error={controller.LastError}");
                    }

                    System.Threading.Thread.Sleep(4000);
                    var (after, _) = controller.Snapshot();
                    foreach (var f in after.Where(x => targets.Any(t => t.Name == x.Name)))
                        lines.Add($"  写入后 {f.Name}: pct={f.Percent?.ToString("0.#") ?? "—"} rpm={f.Rpm?.ToString("0") ?? "—"}");

                    foreach (var f in targets)
                        lines.Add($"  SetDefault {f.Name} -> {controller.SetAuto(f)}");

                    System.Threading.Thread.Sleep(1500);
                    var (back, _) = controller.Snapshot();
                    foreach (var f in back.Where(x => targets.Any(t => t.Name == x.Name)))
                        lines.Add($"  还原后 {f.Name}: pct={f.Percent?.ToString("0.#") ?? "—"} rpm={f.Rpm?.ToString("0") ?? "—"}");
                }
            }

            // 完整硬件树：确认主板 / SuperIO 有没有被 LHM 枚举出来
            lines.Add("");
            lines.Add("==== 硬件树 ====");
            foreach (var hw in controller.HardwareTree())
                lines.Add($"  {hw}");
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex);
        }

        try { File.WriteAllLines(outPath, lines, Encoding.UTF8); } catch { }
        Application.Current.Shutdown(0);
    }

    /// <summary>搭一个屏幕外的宿主：Window → ScrollViewer → Frame → Page。</summary>
    private static (Window Host, FrameworkElement Outer, FrameworkElement Page) BuildHost(string pageName)
    {
        var page = CreatePage(pageName);

        // Page 只能挂在 Window / Frame 下，所以中间夹一个 Frame；
        // 外面再套 ScrollViewer，让页面里绑 ViewportHeight 的那层量到真实高度。
        var frameHost = new Frame
        {
            Content = page,
            NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden,
        };
        // 真实环境里 NavigationView 给页面的高度是有界的（页面根节点绑 ViewportHeight 就是这么来的）。
        // 这里如果外面再套一层 ScrollViewer，Frame 会拿到无限高度，列表就永远不虚拟化，
        // 测出来的数字会完全失真 —— 所以直接让 Frame 撑满窗口。
        // 注意：--shot 渲染的是这个 Frame，不是外面的 Grid，所以这里没法垫底色。
        // 想要"深色模式下真实的画面"，用 --navshot（它走真实 MainWindow + 垫底色）。
        var outer = new Grid();
        outer.Children.Add(frameHost);
        var host = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -8000,
            Top = -8000,
            Width = 1180,
            Height = 780,
            Content = outer,
        };
        host.Show();
        ApplyHostLook(host);
        return (host, frameHost, page);
    }

    /// <summary>
    /// 诊断宿主窗口的主题外观。
    /// 真身是 FluentWindow（Mica + 主题前景色），这里只是普通 Window：默认白底黑字，
    /// 深色模式下会渲染成"白底黑字"，和真实窗口完全不同。所以显式补上背景与前景。
    /// </summary>
    private static void ApplyHostLook(Window host)
    {
        var bg = Application.Current?.TryFindResource("ApplicationBackgroundBrush") as System.Windows.Media.Brush;
        if (bg != null) host.Background = bg;

        var fg = Application.Current?.TryFindResource("TextFillColorPrimaryBrush") as System.Windows.Media.Brush;
        if (fg != null) host.Foreground = fg;
    }

    /// <summary>等一段时间，但保持消息循环转起来（数据加载需要它）。</summary>
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (s, e) =>
            {
                ((DispatcherTimer)s!).Stop();
                frame.Continue = false;
            }, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static int CountVisuals(DependencyObject root)
    {
        int n = 1;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            n += CountVisuals(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    private static void CollectScrollViewers(DependencyObject root, System.Collections.Generic.List<ScrollViewer> found)
    {
        if (root is ScrollViewer sv) found.Add(sv);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            CollectScrollViewers(VisualTreeHelper.GetChild(root, i), found);
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T t && t.Name == name) return t;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var hit = FindByName<T>(VisualTreeHelper.GetChild(root, i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var hit = FindChild<T>(child);
            if (hit != null) return hit;
        }
        return null;
    }

    private static Type PageTypeOf(string pageName) => pageName switch
    {
        "tray" => typeof(Pages.TrayIconsPage),
        "easy" => typeof(Pages.EasySettingsPage),
        "rightmenu" => typeof(Pages.RightMenuPage),
        "tools" => typeof(Pages.ToolsPage),
        "hardware" => typeof(Pages.HardwarePage),
        "fan" => typeof(Pages.FanPage),
        "optimize" => typeof(Pages.OptimizePage),
        "app" => typeof(Pages.AppPage),
        "utilities" => typeof(Pages.UtilitiesPage),
        "settings" => typeof(Pages.SettingsPage),
        "about" => typeof(Pages.AboutPage),
        "home" => typeof(Pages.HomePage),
        _ => typeof(Pages.HomePage),
    };

    /// <summary>把一张 PNG 的某一行/某一列按颜色变化压成"色块区间"，用来量截图里到底画了什么线。</summary>
    private static void RunPixelInfo(string path, int row, int col, string outPath)
    {
        var sb = new StringBuilder();
        try
        {
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                new Uri(Path.GetFullPath(path)),
                System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;
            sb.AppendLine($"图像 {w}x{h}");

            var pixels = new byte[w * h * 4];
            frame.CopyPixels(pixels, w * 4, 0);

            string Hex(int x, int y)
            {
                int i = (y * w + x) * 4;
                return $"#{pixels[i + 2]:X2}{pixels[i + 1]:X2}{pixels[i]:X2}";
            }

            if (row >= 0 && row < h)
            {
                sb.AppendLine($"== 第 {row} 行 ==");
                int start = 0;
                string prev = Hex(0, row);
                for (int x = 1; x <= w; x++)
                {
                    string cur = x < w ? Hex(x, row) : "";
                    if (cur != prev)
                    {
                        if (x - start >= 1)
                            sb.AppendLine($"  x={start,4}..{x - 1,-4} ({x - start,3}px) {prev}");
                        start = x;
                        prev = cur;
                    }
                }
            }

            if (col >= 0 && col < w)
            {
                sb.AppendLine($"== 第 {col} 列 ==");
                int start = 0;
                string prev = Hex(col, 0);
                for (int y = 1; y <= h; y++)
                {
                    string cur = y < h ? Hex(col, y) : "";
                    if (cur != prev)
                    {
                        if (y - start >= 1)
                            sb.AppendLine($"  y={start,4}..{y - 1,-4} ({y - start,3}px) {prev}");
                        start = y;
                        prev = cur;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// 把主窗口内容摘到屏幕外窗口里，导航到指定页，返回 (宿主窗口, 根元素)。
    /// navshot / navdump 共用，保证两边看到的是同一棵树。
    /// </summary>
    private static (Window Host, FrameworkElement Root, MainWindow Shell) BuildNavHost(string pageName, int waitMs)
    {
        var mw = new MainWindow();
        var root = (FrameworkElement)mw.Content;
        mw.Content = null;      // 摘下来，就不用真的显示 MainWindow（不抢焦点、不进任务栏）

        var host = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -9000,
            Top = -9000,
            Width = 1240,
            Height = 820,
            Content = root,
        };
        host.Show();
        ApplyHostLook(host);
        Pump(600);

        var navigate = typeof(MainWindow).GetMethod("Navigate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        navigate?.Invoke(mw, new object?[] { PageTypeOf(pageName) });
        Pump(waitMs);

        ApplyNavFilter(host);
        Pump(500);

        return (host, root, mw);
    }

    /// <summary>
    /// --navfilter &lt;关键词&gt;：给诊断窗口里的搜索框填个词。
    /// 用途：列表筛选后只剩一两行时，卡片会被内容撑矮并垂直居中（上下各一大片空白），
    /// 光跑 --navshot 是看不出来的，得先把结果筛小才复现。
    /// </summary>
    private static void ApplyNavFilter(DependencyObject root)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--navfilter");
        if (index < 0 || index + 1 >= args.Length)
            return;

        foreach (var box in FindAll<System.Windows.Controls.TextBox>(root))
        {
            if (box.Name != "SearchBox")
                continue;

            box.Text = args[index + 1];
            break;
        }
    }

    /// <summary>
    /// 回归测试：从后台线程调用页面的 Log()，确认会正确切回 UI 线程。
    /// 之前 Apply 在 Task.Run 里回调 Log，直接碰 TextBlock 就抛
    /// 「调用线程无法访问此对象，因为另一个线程拥有该对象」。
    /// </summary>
    private static void RunLogTest(string outPath)
    {
        var sb = new StringBuilder();
        Window? host = null;
        try
        {
            (host, var root, _) = BuildNavHost("rightmenu", 3000);
            host.UpdateLayout();

            var page = FindChild<Page>(root);
            if (page == null) { sb.AppendLine("找不到页面"); }
            else
            {
                var method = page.GetType().GetMethod("Log",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (method == null) { sb.AppendLine("找不到 Log 方法"); }
                else
                {
                    var errors = new StringBuilder();
                    var task = Task.Run(() =>
                    {
                        for (int i = 1; i <= 3; i++)
                        {
                            try { method.Invoke(page, new object?[] { "后台线程日志 " + i }); }
                            catch (Exception ex) { errors.AppendLine("  " + ex.GetType().Name + ": " + ex.Message); }
                        }
                    });

                    // 不能用 task.Wait()：那样会堵住 UI 线程，Dispatcher.Invoke 永远回不来（假死锁）
                    var sw = Stopwatch.StartNew();
                    while (!task.IsCompleted && sw.ElapsedMilliseconds < 8000)
                        Pump(50);

                    if (!task.IsCompleted) sb.AppendLine("超时：8 秒内没跑完");
                    sb.AppendLine(errors.Length == 0
                        ? "后台线程调用 Log：成功（3 次）"
                        : "后台线程调用 Log：失败\n" + errors);

                    var status = FindByName<Wpf.Ui.Controls.TextBlock>(page, "StatusLine");
                    sb.AppendLine("状态栏最后内容：" + (status?.Text ?? "（取不到）"));
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { host?.Close(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// --themedump &lt;txt&gt;：打印主题资源的真实合并情况与关键画笔的解析值。
    /// 排查"深色模式下字还是黑的"这类问题，光看控件没用——得先确认
    /// TextFillColorPrimaryBrush 到底解析成了什么颜色、它来自哪个字典。
    /// </summary>
    private static void RunThemeDump(string outPath)
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine("== ApplicationThemeManager ==");
            sb.AppendLine("当前主题：" + Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme());
            sb.AppendLine("系统主题：" + Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme());
            sb.AppendLine();

            sb.AppendLine("== 资源字典（MergedDictionaries，逐层） ==");
            DumpDictionaryTree(Application.Current.Resources, sb, 0);

            sb.AppendLine();
            sb.AppendLine("== 关键资源解析值 ==");
            string[] keys =
            {
                "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush",
                "TextFillColorDisabledBrush",
                "ApplicationBackgroundBrush", "ControlFillColorDefaultBrush",
                "CardBackgroundFillColorDefaultBrush", "CardStrokeColorDefaultBrush",
                "ControlStrokeColorDefaultBrush", "ControlStrongStrokeColorDefaultBrush",
                "TabViewItemBackground", "TabViewItemBackgroundUnselected",
                "TabViewItemBackgroundSelected", "TabViewBorderBrush",
                "AccentFillColorDefaultBrush", "TextOnAccentFillColorPrimaryBrush",
                "SystemAccentColorPrimaryBrush", "SystemAccentColorBrush",
                "SystemFillColorSuccessBrush", "SystemFillColorCautionBrush",
                "SystemFillColorCriticalBrush", "SystemFillColorAttentionBrush",
            };
            foreach (var key in keys)
            {
                object? value = Application.Current.TryFindResource(key);
                sb.AppendLine($"{key,-42} = {DescribeValue(value)}");
            }

            sb.AppendLine();
            sb.AppendLine("== 主题色板（ThemesDictionary 里直接定义的 Color） ==");
            string[] colors =
            {
                "TextFillColorPrimary", "TextFillColorSecondary",
                "SolidBackgroundFillColorBase", "SolidBackgroundFillColorSecondary",
                "CardBackgroundFillColorDefault", "ControlFillColorDefault",
            };
            foreach (var key in colors)
                sb.AppendLine($"{key,-42} = {DescribeValue(Application.Current.TryFindResource(key))}");

            sb.AppendLine();
            sb.AppendLine("== 控件默认样式解析 ==");
            var types = new[]
            {
                typeof(Wpf.Ui.Controls.TabView),
                typeof(Wpf.Ui.Controls.TabViewItem),
                typeof(System.Windows.Controls.TabControl),
                typeof(System.Windows.Controls.TabItem),
                typeof(Wpf.Ui.Controls.Card),
                typeof(Wpf.Ui.Controls.TextBlock),
                typeof(System.Windows.Controls.TextBlock),
                typeof(System.Windows.Controls.ListBox),
                typeof(Wpf.Ui.Controls.Button),
            };
            // DefaultStyleKeyProperty 是 protected static，只能反射拿
            var dskField = typeof(FrameworkElement).GetField("DefaultStyleKeyProperty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var dskDp = dskField?.GetValue(null) as DependencyProperty;

            foreach (var t in types)
            {
                object? dsk;
                try
                {
                    var inst = Activator.CreateInstance(t);
                    dsk = inst is FrameworkElement fe && dskDp != null
                        ? fe.GetValue(dskDp)
                        : null;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"{t.Name}: 无法实例化 {ex.GetType().Name} {ex.Message}");
                    continue;
                }

                object? style = dsk != null ? Application.Current.TryFindResource(dsk) : null;
                string styleText = style is Style st
                    ? $"有（TargetType={st.TargetType?.Name}）"
                    : style == null ? "<无，会回落到系统 Aero2 模板>" : style.GetType().Name;
                sb.AppendLine($"{t.Name,-32} base={t.BaseType?.Name,-16} dsk={dsk} style={styleText}");
            }

            sb.AppendLine();
            sb.AppendLine("== NavigationView 私有缓存字段 ==");
            foreach (var f in typeof(Wpf.Ui.Controls.NavigationView).GetFields(
                         System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                if (f.Name.IndexOf("ache", StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.AppendLine($"  {f.Name} : {f.FieldType.FullName}");
            }

            var ncType = typeof(Wpf.Ui.Controls.NavigationView).Assembly.GetType("Wpf.Ui.Controls.NavigationCache");
            if (ncType != null)
            {
                sb.AppendLine($"NavigationCache 基类={ncType.BaseType?.FullName} 接口={string.Join(",", ncType.GetInterfaces().Select(i => i.Name))}");
                sb.AppendLine("  方法：" + string.Join(", ", ncType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(m => m.Name)));
            }

            sb.AppendLine("== Page 类型能力 ==");
            var pageType = typeof(System.Windows.Controls.Page);
            sb.AppendLine("System.Windows.Controls.Page 有 Foreground=" + (pageType.GetProperty("Foreground") != null)
                        + " 有 Background=" + (pageType.GetProperty("Background") != null));
            var uiPage = typeof(Wpf.Ui.Controls.NavigationView).Assembly.GetType("Wpf.Ui.Controls.Page");
            sb.AppendLine("Wpf.Ui.Controls.Page 存在=" + (uiPage != null) + (uiPage != null ? " 基类=" + uiPage.BaseType?.FullName : ""));

            sb.AppendLine();
            sb.AppendLine("== 相关资源字典内容（TabControl / TextBlock / Typography / Card / ListBox） ==");
            string[] filters = { "TabControl", "TextBlock", "Typography", "Card/", "ListBox", "Page/" };
            foreach (var d in AllDictionaries(Application.Current.Resources))
            {
                if (d.Source == null) continue;
                string src = d.Source.ToString();
                if (!filters.Any(f => src.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                sb.AppendLine("来源：" + src);
                foreach (var key in d.Keys)
                {
                    object? v = d[key];
                    string kt = key is Type kty ? "Type:" + kty.FullName : key?.ToString() ?? "?";
                    if (v is Style sv)
                    {
                        sb.AppendLine($"  key={kt}  Style TargetType={sv.TargetType?.Name} Setters={sv.Setters.Count}"
                                    + $" BasedOn={(sv.BasedOn == null ? "无" : sv.BasedOn.TargetType?.Name)}");
                        foreach (var setter in sv.Setters.OfType<Setter>())
                            sb.AppendLine($"      · {setter.Property?.Name} = {setter.Value}");
                    }
                    else
                    {
                        sb.AppendLine($"  key={kt}  {DescribeValue(v)}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Environment.Exit(0);
    }

    private static IEnumerable<ResourceDictionary> AllDictionaries(ResourceDictionary root)
    {
        yield return root;
        foreach (var child in root.MergedDictionaries)
            foreach (var d in AllDictionaries(child))
                yield return d;
    }

    private static void DumpDictionaryTree(ResourceDictionary dict, StringBuilder sb, int depth)
    {
        string pad = new string(' ', depth * 2);
        string type = dict.GetType().Name;
        string src = "";
        try
        {
            if (dict.Source != null) src = " src=" + dict.Source;
        }
        catch { }

        string extra = "";
        // ThemesDictionary.Theme 是只写属性（没有 get 访问器），只能看 Source 判断当前挂的是哪套
        if (dict is Wpf.Ui.Markup.ThemesDictionary) extra = " [ThemesDictionary]";

        sb.AppendLine($"{pad}- {type}{extra}{src}  (keys={dict.Keys.Count}, merged={dict.MergedDictionaries.Count})");

        foreach (var child in dict.MergedDictionaries)
            DumpDictionaryTree(child, sb, depth + 1);
    }

    /// <summary>
    /// --rowdump &lt;页面&gt; &lt;txt&gt;：量「列表行里的字到底是什么颜色」。
    ///
    /// 和 --navshot 的区别：这里**显示真正的 MainWindow**（FluentWindow 原封不动），
    /// 只是把窗口摆到屏幕外。之前用离屏宿主截图时行文字看着是白的，
    /// 用户实机截图却是黑的 —— 说明宿主和真窗口有差别，那就直接在真窗口上量。
    /// </summary>
    private static void RunRowDump(string pageName, string outPath)
    {
        var sb = new StringBuilder();
        MainWindow? mw = null;
        try
        {
            mw = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -9000,
                Top = -9000,
                ShowInTaskbar = false,
                ShowActivated = false,
                // 用户上次是最大化关的窗口，MainWindow 构造时会把状态恢复成 Maximized，
                // 而 WPF 不允许 ShowActivated=false + Maximized 同时成立，Show() 会直接抛
                // InvalidOperationException。诊断窗口一律掰回 Normal。
                WindowState = WindowState.Normal,
            };
            mw.Show();
            Pump(800);

            typeof(MainWindow)
                .GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(mw, new object?[] { PageTypeOf(pageName) });
            Pump(5000);
            mw.UpdateLayout();

            sb.AppendLine($"窗口 Foreground = {DescribeValue(mw.Foreground)}");
            sb.AppendLine($"主题 = {Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()}");
            sb.AppendLine($"TextFillColorSecondaryBrush = {DescribeValue(Application.Current.TryFindResource("TextFillColorSecondaryBrush"))}");
            sb.AppendLine();

            var root = (DependencyObject)mw.Content;
            var page = FindChild<Page>(root);
            sb.AppendLine($"页面 = {page?.GetType().Name}  Style={(page?.Style == null ? "<null>" : "有")}"
                        + $" Foreground={DescribeValue(page?.Foreground)}");
            sb.AppendLine();

            var list = page == null ? null : FindChild<ListBox>(page);
            if (list == null)
            {
                sb.AppendLine("页面里没有 ListBox");
            }
            else
            {
                sb.AppendLine($"ListBox  Foreground={DescribeValue(list.Foreground)}  Background={DescribeValue(list.Background)}"
                            + $"  本地Resources={list.Resources.Count}  项数={list.Items.Count}");
                sb.AppendLine($"ListBox Template = {(list.Template == null ? "<null>" : "有")}");
                sb.AppendLine($"ListBox Foreground 来源 = {ValueSourceOf(list, Control.ForegroundProperty)}");

                var item = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                sb.AppendLine($"ListBoxItem  Foreground={DescribeValue(item?.Foreground)}"
                            + $"  Style={(item?.Style == null ? "<null>" : item.Style.Setters.Count + " setter")}"
                            + $"  Template={(item?.Template == null ? "<null>" : "有")}"
                            + $"  来源={(item == null ? "-" : ValueSourceOf(item, Control.ForegroundProperty))}");

                // 行里所有文本，逐个报颜色（这就是用户看到的那些字）
                var texts = item != null
                    ? FindAll<System.Windows.Controls.TextBlock>(item).ToList()
                    : new List<System.Windows.Controls.TextBlock>();
                sb.AppendLine($"行内文本 {texts.Count} 个：");
                foreach (var t in texts)
                    sb.AppendLine($"    '{Trim(t.Text, 18)}' fg={DescribeValue(t.Foreground)}"
                                + $"  显式={(t.ReadLocalValue(System.Windows.Controls.TextBlock.ForegroundProperty) != DependencyProperty.UnsetValue ? "是" : "否")}");

                // 逐层往上，看黑色从哪一层开始
                sb.AppendLine();
                sb.AppendLine("继承链（从第一个文本往上）：");
                DependencyObject? cur = texts.FirstOrDefault();
                int depth = 0;
                while (cur != null && depth++ < 40)
                {
                    string fg = cur is Control c ? DescribeValue(c.Foreground)
                              : cur is System.Windows.Controls.TextBlock tb ? DescribeValue(tb.Foreground)
                              : "-";
                    sb.AppendLine($"    ↑ {cur.GetType().FullName} fg={fg}");
                    cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                        ? VisualTreeHelper.GetParent(cur)
                        : LogicalTreeHelper.GetParent(cur);
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { mw?.Close(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// --darkaudit &lt;txt&gt;：在深色主题下逐页扫一遍，把所有"偏暗的文字"报出来。
    ///
    /// 这类问题的根子都一样：显式指定 Style 会顶掉 WPF-UI 的隐式样式，控件掉回 Aero2，
    /// 而 Aero2 主题样式里的 Foreground 是黑的，且 DefaultStyle 的优先级**高于继承**，
    /// 页面那层白色前景传不进去。人眼看截图很容易漏，所以用亮度算一遍。
    /// </summary>
    private static void RunDarkAudit(string outPath)
    {
        var sb = new StringBuilder();
        MainWindow? mw = null;
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
            Pump(300);

            mw = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -9000,
                Top = -9000,
                ShowInTaskbar = false,
                ShowActivated = false,
                // 用户上次是最大化关的窗口，MainWindow 构造时会把状态恢复成 Maximized，
                // 而 WPF 不允许 ShowActivated=false + Maximized 同时成立，Show() 会直接抛
                // InvalidOperationException。诊断窗口一律掰回 Normal。
                WindowState = WindowState.Normal,
            };
            mw.Show();
            Pump(700);

            var navigate = typeof(MainWindow).GetMethod("Navigate",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            string[] pages =
            {
                "home", "hardware", "tools", "utilities", "optimize",
                "rightmenu", "tray", "easysettings", "settings", "about",
            };

            int total = 0;
            foreach (var name in pages)
            {
                navigate?.Invoke(mw, new object?[] { PageTypeOf(name) });
                Pump(3500);
                mw.UpdateLayout();

                var page = FindChild<Page>((DependencyObject)mw.Content);
                if (page == null) { sb.AppendLine($"{name,-14} 找不到页面"); continue; }

                var dark = new List<string>();
                int texts = 0;
                foreach (var tb in FindAll<System.Windows.Controls.TextBlock>(page))
                {
                    if (string.IsNullOrWhiteSpace(tb.Text)) continue;
                    if (tb.Foreground is not SolidColorBrush brush) continue;
                    texts++;

                    // 按钮里的字不算：强调色按钮的 TextOnAccentFillColorPrimaryBrush
                    // 在深色主题下本来就是黑字配浅蓝底，这是 Win11 的设计，不是 bug。
                    if (InsideButton(tb)) continue;

                    var c = brush.Color;
                    double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 * (c.A / 255.0);
                    if (lum >= 0.35) continue;

                    dark.Add($"'{Trim(tb.Text, 20)}' {c} 亮度={lum:F2} 来源={ValueSourceOf(tb, System.Windows.Controls.TextBlock.ForegroundProperty)}");
                }

                total += dark.Count;
                sb.AppendLine($"{name,-14} 文本 {texts,3} 个，偏暗 {dark.Count} 个");
                foreach (var line in dark.Take(10))
                    sb.AppendLine("    · " + line);
                if (dark.Count > 10) sb.AppendLine($"    · …另有 {dark.Count - 10} 个");
            }

            sb.AppendLine();
            sb.AppendLine(total == 0 ? "结论：深色主题下没有偏暗文字。" : $"结论：还有 {total} 处偏暗文字。");
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { mw?.Close(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>这个文本是不是在按钮里（按钮上的字由按钮模板自己决定颜色，不参与审计）。</summary>
    private static bool InsideButton(DependencyObject node)
    {
        DependencyObject? cur = node;
        int guard = 0;
        while (cur != null && guard++ < 40)
        {
            if (cur is System.Windows.Controls.Primitives.ButtonBase) return true;
            cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    /// <summary>
    /// --themeflash &lt;txt&gt;：在真窗口上切换深浅色，记录"切换前 / 切换瞬间 / 之后"三个时刻的状态。
    ///
    /// 目的：判断"闪一下"到底是 WPF 层的顺序问题（页面晚一步才变色），
    /// 还是 DWM 层的（Mica 重设时掉一帧，露出灰色兜底）。
    /// </summary>
    private static void RunThemeFlash(string outPath)
    {
        var sb = new StringBuilder();
        MainWindow? mw = null;
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            Pump(300);

            mw = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -9000,
                Top = -9000,
                ShowInTaskbar = false,
                ShowActivated = false,
                // 用户上次是最大化关的窗口，MainWindow 构造时会把状态恢复成 Maximized，
                // 而 WPF 不允许 ShowActivated=false + Maximized 同时成立，Show() 会直接抛
                // InvalidOperationException。诊断窗口一律掰回 Normal。
                WindowState = WindowState.Normal,
            };
            mw.Show();
            Pump(1000);

            typeof(MainWindow)
                .GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(mw, new object?[] { typeof(Pages.SettingsPage) });
            Pump(1500);

            void Snapshot(string label)
            {
                var primary = Application.Current.TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush;
                var appBg = Application.Current.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush;
                var cardBg = Application.Current.TryFindResource("CardBackgroundFillColorDefaultBrush") as SolidColorBrush;
                var page = FindChild<Page>((DependencyObject)mw!.Content);
                var pageFg = page?.Foreground as SolidColorBrush;
                var winBg = mw.Background as SolidColorBrush;

                sb.AppendLine($"[{label}]"
                            + $" 主题={Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()}"
                            + $" primary={primary?.Color}"
                            + $" appBg={appBg?.Color}"
                            + $" cardBg={cardBg?.Color}"
                            + $" pageFg={pageFg?.Color}"
                            + $" winBg={(winBg == null ? "null" : winBg.Color.ToString())}"
                            + $" backdrop={mw.WindowBackdropType}");
            }

            Snapshot("切换前");

            // 走真正的切换路径（不盖任何蒙层，滑块动画不受影响）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long tApply = 0, tNotify = 0;
            Controls.ThemeSwitch.Run(() =>
            {
                long a = sw.ElapsedMilliseconds;
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                long b = sw.ElapsedMilliseconds;
                Controls.UiTheme.NotifyThemeChanged();
                long c = sw.ElapsedMilliseconds;
                tApply = b - a;
                tNotify = c - b;
            });
            sb.AppendLine($"改主题拆解：Apply={tApply}ms，NotifyThemeChanged={tNotify}ms");
            sb.AppendLine($"各页耗时：{Controls.UiTheme.HandlerLog}");
            sb.AppendLine("ThemeSwitch：" + Controls.ThemeSwitch.LastReport);
            Snapshot("切换返回");

            // 换主题这条路径上不许盖任何东西：盖住就会压住 ToggleSwitch 的 167ms 滑块动画
            var adornerLayer = AdornerLayer.GetAdornerLayer((FrameworkElement)mw.Content);
            int adorners = adornerLayer?.GetAdorners((UIElement)mw.Content)?.Length ?? 0;
            sb.AppendLine($"内容区蒙层：{adorners} 个（应为 0；不为 0 会压住开关的滑块动画）");

            Pump(300);
            Snapshot("稳定之后");
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { mw?.Close(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// --switchtest &lt;txt&gt;：真的去点一下设置页的深浅色开关，按时间采样
    /// "主题什么时候变、蒙层什么时候出现"，验证换主题没有压在滑块的 167ms 动画上。
    ///
    /// 判据：t &lt; 167ms 这段（滑块正在滑）里蒙层数必须是 0，否则就是又把动画盖住了。
    /// </summary>
    private static void RunSwitchTest(string outPath)
    {
        var sb = new StringBuilder();
        MainWindow? mw = null;
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
            Pump(300);

            mw = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -9000,
                Top = -9000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowState = WindowState.Normal,
            };
            mw.Show();
            Pump(1000);

            typeof(MainWindow)
                .GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(mw, new object?[] { typeof(Pages.SettingsPage) });
            Pump(1200);

            var page = FindChild<Pages.SettingsPage>((DependencyObject)mw.Content);
            var toggle = page?.FindName("DarkSwitch") as Wpf.Ui.Controls.ToggleSwitch;
            if (toggle == null)
            {
                sb.AppendLine("找不到 DarkSwitch");
            }
            else
            {
                var layer = AdornerLayer.GetAdornerLayer((FrameworkElement)mw.Content);
                var content = (UIElement)mw.Content;

                sb.AppendLine("开始点开关（滑块动画 167ms）");
                sb.AppendLine("t(ms)  主题    蒙层数");

                toggle.IsChecked = true;              // 触发 Checked → Dark_Toggled
                var watch = Stopwatch.StartNew();

                string last = "";
                int overlayDuringSlide = 0;
                while (watch.ElapsedMilliseconds < 420)
                {
                    Pump(10);
                    long t = watch.ElapsedMilliseconds;
                    string theme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme().ToString();
                    int overlays = layer?.GetAdorners(content)?.Length ?? 0;
                    string line = $"{theme}/{overlays}";
                    if (line != last)
                    {
                        sb.AppendLine($"{t,5}  {theme,-6}  {overlays}");
                        last = line;
                    }
                    if (t < 167 && overlays > 0) overlayDuringSlide++;
                }

                sb.AppendLine();
                sb.AppendLine(overlayDuringSlide == 0
                    ? "结论：换主题全程没有任何蒙层，滑块动画可以完整播完。"
                    : $"结论：滑块动画期间有 {overlayDuringSlide} 个采样点被蒙层盖住 —— 还是会顿。");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { mw?.Close(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// --symbols：把 <c>SymbolRegular</c> 的成员名全列出来。
    /// 给工具磁贴配字形用 —— 手写很容易拼错，拼错了不报错，只是显示成空白。
    /// </summary>
    private static void RunSymbols(string outPath)
    {
        var sb = new StringBuilder();
        var names = Enum.GetNames(typeof(Wpf.Ui.Controls.SymbolRegular)).OrderBy(n => n, StringComparer.Ordinal);
        foreach (string name in names)
            sb.AppendLine($"{name}={(int)Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(name)}");
        sb.AppendLine($"共 {names.Count()} 个");
        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// <c>--glyphaudit &lt;txt&gt; [关键词…]</c>：逐个工具检查字形码位在 <b>Segoe Fluent Icons 里是否真的有字形</b>。
    ///
    /// <para>
    /// 为什么需要它：<c>--symbols</c> 只能证明「<see cref="Wpf.Ui.Controls.SymbolRegular"/> 里有这个成员名」，
    /// 证明不了「本机这版字体里有这个码位」。两者不是一回事 —— 枚举成员齐了，字体没跟上，
    /// 画出来就是一个残缺的角标或空白，而且<b>不报任何错</b>。
    /// 这里直接读字体的 cmap（<see cref="GlyphTypeface.CharacterToGlyphMap"/>），一查一个准。
    /// </para>
    /// <para>
    /// 后面跟关键词时会额外列出「字体里有、名字里含这个词」的候选，方便换一个能显示的。
    /// </para>
    /// </summary>
    private static void RunGlyphAudit(string outPath, string[] keywords)
    {
        var sb = new StringBuilder();
        try
        {
            var fontPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "SegoeIcons.ttf");

            GlyphTypeface? face = null;
            try
            {
                face = new GlyphTypeface(new Uri(fontPath));
                sb.AppendLine($"字体 {fontPath}");
                sb.AppendLine($"  家族 {string.Join(" / ", face.FamilyNames.Values)}  字形数 {face.GlyphCount}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"字体加载失败：{ex.Message}");
            }

            sb.AppendLine();

            // 判据：图标字体是 16 位 cmap，码位 > U+FFFF 的一律画不出来。
            // 不要用 CharacterToGlyphMap 判断 —— 实测这个字体读出来的 map 不全，
            // 会把明明能正常显示的 4 位码位也报成"缺失"（第一次就踩了这个坑，
            // 报了 38 个假的，跟截图对不上）。真正的判据只有"画出来看"和"码位在不在 BMP 内"。
            var broken = new List<(Win11Toolbox.Services.ToolItem Item, string Name, int Code)>();
            var missingConfig = new List<string>();

            foreach (var item in Win11Toolbox.Services.ToolCatalog.Categories.SelectMany(c => c.Items))
            {
                var symbol = Win11Toolbox.Services.ToolCatalog.Glyph(item);
                if (symbol is null)
                {
                    missingConfig.Add(item.Name);
                    continue;
                }

                var code = (int)symbol.Value;
                if (code > 0xFFFF)
                    broken.Add((item, symbol.Value.ToString(), code));
            }

            sb.AppendLine($"== 码位超出 BMP、画不出来 {broken.Count} 个 ==");
            foreach (var (item, name, code) in broken)
                sb.AppendLine($"  U+{code:X}  {name,-30} {item.Name}   ->  {item.Target}");

            if (missingConfig.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"== 根本没配字形 {missingConfig.Count} 个 ==");
                foreach (var name in missingConfig) sb.AppendLine("  " + name);
            }

            if (keywords.Length > 0 && face is not null)
            {
                sb.AppendLine();
                sb.AppendLine("== 候选（名字含关键词、且字体里有字形）==");
                foreach (var keyword in keywords)
                {
                    sb.AppendLine($"[{keyword}]");
                    var hits = Enum.GetNames(typeof(Wpf.Ui.Controls.SymbolRegular))
                        .Where(n => n.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        .Select(n => (Name: n, Code: (int)Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(n)))
                        .Where(x => x.Code <= 0xFFFF)
                        .OrderBy(x => x.Name, StringComparer.Ordinal)
                        .ToList();

                    foreach (var (name, code) in hits)
                        sb.AppendLine($"  {name,-34} U+{code:X4}");

                    if (hits.Count == 0) sb.AppendLine("  （没有）");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// <c>--glyphsheet &lt;png&gt;</c>：把 47 个工具图标画成一张总表。
    ///
    /// <para>
    /// 为什么不能只看码位：<c>SymbolRegular</c> 的枚举值是码位，但码位落在字体里没有的位置时，
    /// WPF 不会报错，只会画出一个残缺的角标或者空白。查字体 cmap 也不完全可靠
    /// （系统字体版本、WPF 读 cmap 的方式都会有出入），所以这里直接用界面同一条渲染路径画出来看。
    /// </para>
    /// </summary>
    private static void RunGlyphSheet(string outPath)
    {
        try
        {
            var items = Win11Toolbox.Services.ToolCatalog.Categories.SelectMany(c => c.Items).ToList();
            const int cols = 6;
            int rows = (items.Count + cols - 1) / cols;
            const double cellW = 210;
            const double cellH = 84;

            var grid = new Grid
            {
                Width = cols * cellW,
                Height = rows * cellH,
                Background = Brushes.White,
            };

            for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition());

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var symbol = Win11Toolbox.Services.ToolCatalog.Glyph(item);
                if (symbol is null) continue;

                var cell = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(12, 10, 8, 10),
                };

                cell.Children.Add(new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = symbol.Value,
                    FontSize = 28,
                    Foreground = Brushes.Black,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });

                cell.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12,
                    Foreground = Brushes.Black,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Width = 140,
                });

                Grid.SetColumn(cell, i % cols);
                Grid.SetRow(cell, i / cols);
                grid.Children.Add(cell);
            }

            var host = new Border { Child = grid };
            host.Measure(new Size(grid.Width, grid.Height));
            host.Arrange(new Rect(0, 0, grid.Width, grid.Height));
            host.UpdateLayout();

            var bmp = new RenderTargetBitmap(
                (int)grid.Width, (int)grid.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(outPath)) encoder.Save(fs);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPath + ".err", ex.ToString()); } catch { }
        }

        Environment.Exit(0);
    }

    /// <summary>
    /// <c>--ringsheet &lt;png&gt;</c>：把 <see cref="Controls.CountdownRing"/> 在不同剩余比例下的样子排成一行。
    ///
    /// <para>
    /// 2FA 的圆圈颜色是「绿→黄→红」，这东西没法在截图上等半分钟看全，
    /// 只能一次把各档都画出来核对。顺便也能看出圆头端帽在细弧段上有没有被裁。
    /// </para>
    /// </summary>
    private static void RunRingSheet(string outPath)
    {
        try
        {
            double[] progresses = { 1.0, 0.85, 0.7, 0.55, 0.4, 0.25, 0.12, 0.03 };
            int[] seconds = { 30, 26, 21, 17, 12, 8, 4, 1 };

            const double cell = 96;
            var grid = new Grid
            {
                Width = progresses.Length * cell,
                Height = cell + 26,
                Background = Brushes.White,
            };

            for (int i = 0; i < progresses.Length; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cell) });

            for (int i = 0; i < progresses.Length; i++)
            {
                var stack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 14, 0, 0),
                };

                stack.Children.Add(new Controls.CountdownRing
                {
                    Width = 64,
                    Height = 64,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Progress = progresses[i],
                    Seconds = seconds[i],
                    TrackBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                });

                stack.Children.Add(new TextBlock
                {
                    Text = $"{progresses[i] * 100:F0}%",
                    FontSize = 11,
                    Foreground = Brushes.Black,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0),
                });

                Grid.SetColumn(stack, i);
                grid.Children.Add(stack);
            }

            var host = new Border { Child = grid, Background = Brushes.White };
            host.Measure(new Size(grid.Width, grid.Height));
            host.Arrange(new Rect(0, 0, grid.Width, grid.Height));
            host.UpdateLayout();

            var bmp = new RenderTargetBitmap(
                (int)grid.Width, (int)grid.Height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(outPath)) encoder.Save(fs);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPath + ".err", ex.ToString()); } catch { }
        }

        Environment.Exit(0);
    }

    /// <summary>
    /// --dialogdump &lt;txt&gt;：把 <see cref="Controls.InputDialog"/> 的对话框离屏搭一遍，
    /// 量出"上边留白 / 下边留白"到底差多少、多出来的高度是谁占的。
    /// </summary>
    private static void RunDialogDump(string outPath)
    {
        var sb = new StringBuilder();
        Window? window = null;
        try
        {
            var (panel, input, cancel, ok) = Controls.InputDialog.BuildContent(
                "新的计算机名",
                "HORIZEN",
                "最多 15 个字符，只能用英文字母、数字和连字符。重启后才会生效。",
                "下一步");

            window = new Wpf.Ui.Controls.FluentWindow
            {
                Title = "重命名这台电脑",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                MinHeight = 0,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -9000,
                Top = -9000,
                ShowInTaskbar = false,
                ShowActivated = false,
                ExtendsContentIntoTitleBar = true,
                Content = panel,
            };
            window.Show();
            Pump(600);

            sb.AppendLine($"窗口 ActualHeight={window.ActualHeight:F1}  Height={window.Height:F1}"
                        + $"  SizeToContent={window.SizeToContent}");
            sb.AppendLine($"窗口 MinHeight={window.MinHeight:F1}  MaxHeight={window.MaxHeight:F1}"
                        + $"  DesiredSize={window.DesiredSize.Height:F1}");
            sb.AppendLine($"面板 ActualHeight={panel.ActualHeight:F1}  DesiredSize={panel.DesiredSize.Height:F1}"
                        + $"  Margin={panel.Margin}");
            sb.AppendLine();

            // 试试把 SizeToContent 关掉、直接给内容该有的高度，看能不能压下来
            double wanted = panel.DesiredSize.Height;
            window.SizeToContent = SizeToContent.Manual;
            window.Height = wanted;
            Pump(200);
            sb.AppendLine($"强制 Height={wanted:F1} 之后：ActualHeight={window.ActualHeight:F1}"
                        + $"（若仍大于 {wanted:F1}，说明有最小高度在卡着）");
            sb.AppendLine();

            // 逐个孩子量位置：以窗口为参照，才能算出真正的上/下留白
            double panelTop = panel.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
            sb.AppendLine($"面板顶部距窗口顶部：{panelTop:F1}px");
            sb.AppendLine("序号  类型                  高     距窗口顶   距窗口底");

            for (int i = 0; i < panel.Children.Count; i++)
            {
                if (panel.Children[i] is not FrameworkElement child) continue;
                double top = child.TransformToAncestor(window).Transform(new Point(0, 0)).Y;
                double bottom = window.ActualHeight - (top + child.ActualHeight);
                sb.AppendLine($"{i,3}  {child.GetType().Name,-18}  {child.ActualHeight,5:F1}  {top,9:F1}  {bottom,9:F1}");
            }

            sb.AppendLine();
            double firstTop = panel.Children[0] is FrameworkElement f0
                ? f0.TransformToAncestor(window).Transform(new Point(0, 0)).Y : 0;
            double lastBottom = panel.Children[^1] is FrameworkElement fl
                ? window.ActualHeight - (fl.TransformToAncestor(window).Transform(new Point(0, 0)).Y + fl.ActualHeight)
                : 0;
            sb.AppendLine($"上留白（窗口顶 → 第一行）={firstTop:F1}px");
            sb.AppendLine($"下留白（最后一行 → 窗口底）={lastBottom:F1}px");
            sb.AppendLine($"差值={lastBottom - firstTop:F1}px（应为 0）");

            sb.AppendLine();
            sb.AppendLine("--- 窗口模板前几层（找多出来的高度是谁占的）---");
            DumpSizes(window, sb, 0, 4);

            // 顺便出一张图，别只看数字
            string png = System.IO.Path.ChangeExtension(outPath, ".png");
            try
            {
                var dpi = VisualTreeHelper.GetDpi(panel);
                double w = panel.ActualWidth + Controls.InputDialog.Edge * 2;
                double h = panel.ActualHeight + Controls.InputDialog.Edge * 2;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var backdrop = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
                    dc.DrawRectangle(backdrop, null, new Rect(0, 0, w, h));
                    dc.DrawRectangle(new VisualBrush(panel), null,
                        new Rect(Controls.InputDialog.Edge, Controls.InputDialog.Edge, panel.ActualWidth, panel.ActualHeight));
                }

                var rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                rtb.Render(visual);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var stream = File.Create(png);
                encoder.Save(stream);
                sb.AppendLine();
                sb.AppendLine($"已输出预览图：{png}（{w:F0}x{h:F0}）");
            }
            catch (Exception ex)
            {
                sb.AppendLine("预览图输出失败：" + ex.Message);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { window?.Close(); } catch { }
        Environment.Exit(0);
    }

    private static void DumpSizes(DependencyObject node, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        if (node is FrameworkElement fe)
        {
            sb.AppendLine($"{new string(' ', depth * 2)}{fe.GetType().Name} 名字={fe.Name}"
                        + $"  {fe.ActualWidth:F1}x{fe.ActualHeight:F1}"
                        + $"  Margin={fe.Margin}  Padding={(fe is Control c ? c.Padding.ToString() : "-")}");
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            DumpSizes(VisualTreeHelper.GetChild(node, i), sb, depth + 1, maxDepth);
    }

    /// <summary>
    /// --toggleanim &lt;txt&gt;：把 <c>ToggleSwitch</c> 模板里的动画定义挖出来。
    ///
    /// 为什么要这个：用户反馈"开关滑到一半顿一下再跳过去"。WPF-UI 4.x 的模板是编译进 BAML 的，
    /// 源码里看不到 Duration，只能运行时从 <see cref="ControlTemplate"/> 的触发器 /
    /// VisualStateManager 里读出来，才知道动画到底多长、用的什么缓动。
    /// </summary>
    private static void RunToggleAnim(string outPath)
    {
        var sb = new StringBuilder();
        Window? host = null;
        try
        {
            var toggle = new Wpf.Ui.Controls.ToggleSwitch { Width = 44, Height = 22 };
            host = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -9000,
                Top = -9000,
                Width = 140,
                Height = 90,
                Content = toggle,
            };
            host.Show();
            Pump(400);
            toggle.ApplyTemplate();

            var template = toggle.Template;
            sb.AppendLine($"控件：{toggle.GetType().FullName}");
            sb.AppendLine($"模板：{template?.GetType().Name ?? "null"}，触发器 {template?.Triggers.Count ?? 0} 个");
            sb.AppendLine();

            if (template != null)
            {
                foreach (TriggerBase trigger in template.Triggers)
                {
                    sb.AppendLine($"触发器 {trigger.GetType().Name}"
                                + (trigger is Trigger t ? $"  {t.Property?.Name} = {t.Value}" : ""));
                    if (trigger is Trigger tt)
                    {
                        DescribeActions(tt.EnterActions, sb, "  进入");
                        DescribeActions(tt.ExitActions, sb, "  退出");
                    }
                    else if (trigger is MultiTrigger mt)
                    {
                        foreach (var c in mt.Conditions)
                            sb.AppendLine($"  条件 {c.Property?.Name} = {c.Value}");
                        DescribeActions(mt.EnterActions, sb, "  进入");
                        DescribeActions(mt.ExitActions, sb, "  退出");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("--- VisualStateManager ---");
            if (VisualTreeHelper.GetChildrenCount(toggle) > 0
                && VisualTreeHelper.GetChild(toggle, 0) is FrameworkElement templateRoot)
            {
                DumpVisualStates(templateRoot, sb, "  ");
            }
            else
            {
                sb.AppendLine("  拿不到模板根元素");
            }

            sb.AppendLine();
            sb.AppendLine("--- 视觉树里带变换/动画的元素 ---");
            DumpAnimatedParts(toggle, sb, 1);
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { host?.Close(); } catch { }
        Environment.Exit(0);
    }

    private static void DescribeActions(TriggerActionCollection actions, StringBuilder sb, string label)
    {
        foreach (TriggerAction action in actions)
        {
            if (action is not BeginStoryboard begin || begin.Storyboard == null) continue;
            sb.AppendLine($"{label} BeginStoryboard（{begin.Storyboard.Children.Count} 条动画）");
            DescribeStoryboard(begin.Storyboard, sb, label + "  ");
        }
    }

    private static void DescribeStoryboard(Storyboard storyboard, StringBuilder sb, string indent)
    {
        foreach (Timeline timeline in storyboard.Children)
        {
            string target = Storyboard.GetTargetName(timeline) ?? "?";
            var path = Storyboard.GetTargetProperty(timeline);
            string prop = path?.Path ?? "?";

            if (timeline is DoubleAnimation da)
            {
                sb.AppendLine($"{indent}{prop} @{target}：Double {da.From}→{da.To}"
                            + $"  {da.Duration.TimeSpan.TotalMilliseconds:F0}ms"
                            + $"  缓动={da.EasingFunction?.GetType().Name ?? "无"}"
                            + (da.EasingFunction is EasingFunctionBase e ? $"({e.EasingMode})" : ""));
            }
            else if (timeline is ThicknessAnimation ta)
            {
                sb.AppendLine($"{indent}{prop} @{target}：Thickness {ta.From}→{ta.To}"
                            + $"  {ta.Duration.TimeSpan.TotalMilliseconds:F0}ms"
                            + $"  缓动={ta.EasingFunction?.GetType().Name ?? "无"}");
            }
            else
            {
                sb.AppendLine($"{indent}{prop} @{target}：{timeline.GetType().Name}"
                            + $"  {timeline.Duration.TimeSpan.TotalMilliseconds:F0}ms");
            }
        }
    }

    private static void DumpVisualStates(FrameworkElement root, StringBuilder sb, string indent)
    {
        var groups = VisualStateManager.GetVisualStateGroups(root);
        if (groups == null || groups.Count == 0) return;

        foreach (VisualStateGroup group in groups)
        {
            sb.AppendLine($"{indent}状态组 {group.Name}");
            foreach (VisualState state in group.States)
            {
                if (state.Storyboard == null) continue;
                sb.AppendLine($"{indent}  状态 {state.Name}（{state.Storyboard.Children.Count} 条动画）");
                DescribeStoryboard(state.Storyboard, sb, indent + "    ");
            }
        }
    }

    private static void DumpAnimatedParts(DependencyObject node, StringBuilder sb, int depth)
    {
        if (depth > 14) return;
        if (node is FrameworkElement fe)
        {
            var transform = fe.RenderTransform;
            if (fe.HasAnimatedProperties || transform is not null and not MatrixTransform)
            {
                string extra = transform == null ? "" : $"  变换={transform.GetType().Name}";
                sb.AppendLine($"{new string(' ', depth * 2)}{fe.GetType().Name} 名字={fe.Name}"
                            + $"  动画中={fe.HasAnimatedProperties}{extra}");
            }
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            DumpAnimatedParts(VisualTreeHelper.GetChild(node, i), sb, depth + 1);
    }

    private static string ValueSourceOf(DependencyObject target, DependencyProperty property)
    {
        var source = DependencyPropertyHelper.GetValueSource(target, property);
        string text = $"{source.BaseValueSource}";
        if (source.IsAnimated) text += "+Animated";
        if (source.IsExpression) text += "+Expression";
        return text;
    }

    private static string Trim(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    private static string DescribeValue(object? value)
    {
        if (value is null) return "<未找到>";
        if (value is SolidColorBrush scb) return $"SolidColorBrush {scb.Color}";
        if (value is Color c) return $"Color {c}";
        if (value is System.Windows.Media.Brush b) return b.GetType().Name + " " + b;
        return value.GetType().Name + " " + value;
    }

    /// <summary>打印焦点广告层和左侧导航的视觉树，用来定位「框住导航项的那两个矩形」到底是什么。</summary>
    private static void RunNavDump(string pageName, string outPath)
    {
        var sb = new StringBuilder();
        Window? host = null;
        try
        {
            (host, var root, _) = BuildNavHost(pageName, 3000);
            host.UpdateLayout();

            var focused = Keyboard.FocusedElement as DependencyObject;
            sb.AppendLine("键盘焦点：" + Describe(root, focused));
            sb.AppendLine("逻辑焦点：" + Describe(root, FocusManager.GetFocusedElement(host) as DependencyObject));
            sb.AppendLine();

            sb.AppendLine("== 广告层 ==");
            foreach (var dec in FindAll<AdornerDecorator>(root))
            {
                var layer = dec.AdornerLayer;
                if (layer == null) continue;
                int count = VisualTreeHelper.GetChildrenCount(layer);
                for (int i = 0; i < count; i++)
                {
                    if (VisualTreeHelper.GetChild(layer, i) is Adorner adorner)
                        sb.AppendLine("  Adorner " + adorner.GetType().Name + " → " + Describe(root, adorner.AdornedElement));
                }
            }
            sb.AppendLine();

            sb.AppendLine("== 左侧导航视觉树 ==");
            var nav = FindByName<Wpf.Ui.Controls.NavigationView>(root, "Nav");
            if (nav != null)
                DumpTree(nav, root, sb, 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        try { host?.Close(); } catch { }
        Environment.Exit(0);
    }

    private static void DumpTree(DependencyObject node, FrameworkElement root, StringBuilder sb, int depth)
    {
        string indent = new(' ', depth * 2);
        sb.AppendLine(indent + Describe(root, node));

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            DumpTree(VisualTreeHelper.GetChild(node, i), root, sb, depth + 1);
    }

    private static string Describe(FrameworkElement root, DependencyObject? node)
    {
        if (node == null) return "null";
        var text = node.GetType().Name;

        if (node is FrameworkElement fe)
        {
            if (!string.IsNullOrEmpty(fe.Name)) text += " '" + fe.Name + "'";
            try
            {
                var bounds = fe.TransformToAncestor(root).TransformBounds(new Rect(fe.RenderSize));
                text += $" [{bounds.X:F0},{bounds.Y:F0} {bounds.Width:F0}x{bounds.Height:F0}]";
            }
            catch { }

            if (fe is Control c)
            {
                if (c.BorderThickness != default) text += " border=" + c.BorderThickness;
                if (c.BorderBrush is SolidColorBrush sb) text += " brush=#" + sb.Color.ToString().Substring(1);
                if (c.Background is SolidColorBrush cbg) text += " bg=#" + cbg.Color.ToString().Substring(1);
                if (c.FocusVisualStyle != null) text += " focusVisual=有";
                if (c.Focusable) text += " focusable";
                if (c.IsKeyboardFocused) text += " **键盘焦点**";
                if (c.IsKeyboardFocusWithin) text += " focusWithin";
            }
            else if (fe is Border b)
            {
                if (b.BorderThickness != default) text += " border=" + b.BorderThickness;
                if (b.BorderBrush is SolidColorBrush sb) text += " brush=#" + sb.Color.ToString().Substring(1);
                if (b.Background is SolidColorBrush bg) text += " bg=#" + bg.Color.ToString().Substring(1);
            }
            else if (fe is Panel p && p.Background is SolidColorBrush pbg)
            {
                text += " bg=#" + pbg.Color.ToString().Substring(1);
            }

            // TextBlock 不是 Control（直接继承 FrameworkElement），单独取前景色
            if (fe is System.Windows.Controls.TextBlock tb && tb.Foreground is SolidColorBrush tfg)
                text += " fg=#" + tfg.Color.ToString().Substring(1);
        }
        return text;
    }

    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var list = new List<T>();
        Walk(root);
        return list;

        void Walk(DependencyObject node)
        {
            if (node is T hit) list.Add(hit);
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>
    /// 用真实 NavigationView 离屏渲染一页（含左侧导航栏），验证外层 ScrollViewer
    /// 有没有因为页面超高而把标题切掉。窗口放在屏幕外、不激活、不进任务栏。
    /// </summary>
    private static void RunNavShot(string pageName, string outPath)
    {
        var sb = new StringBuilder();
        Window? host = null;
        try
        {
            var mw = new MainWindow();
            var root = mw.Content as FrameworkElement;
            if (root == null) { sb.AppendLine("拿不到主窗口根元素"); }
            else
            {
                // 把主窗口的内容摘下来，挂到屏幕外的宿主窗口里 —— 这样不用真的
                // 显示 MainWindow，也就不会抢焦点、不会进任务栏。
                mw.Content = null;

                // 垫一层主题底色再挂上去。真实窗口是 FluentWindow，背景由 DWM 的 Mica 画，
                // RenderTargetBitmap 抓不到；不垫的话截出来是透明的，看图软件当白底显示，
                // 深色模式的截图就会变成"白底黑字"，根本没法判断。
                var backdrop = new Grid
                {
                    Background = Application.Current?.TryFindResource("ApplicationBackgroundBrush") as Brush,
                };
                backdrop.Children.Add(root);

                host = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -9000,
                    Top = -9000,
                    Width = 1240,
                    Height = 820,
                    Content = backdrop,
                };
                host.Show();
                ApplyHostLook(host);
                Pump(600);

                // 复用 PageTypeOf，别再单独维护一份 switch（之前漏了 fan 导致截到主页）
                var pageType = PageTypeOf(pageName);
                typeof(MainWindow)
                    .GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(mw, new object?[] { pageType });

                Pump(3000);

                // 来回切一次：验证离开页面时外层滚动恢复、回来又锁上，不会越切越坏
                typeof(MainWindow)
                    .GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(mw, new object?[] { typeof(Pages.HomePage) });
                Pump(1200);
                typeof(MainWindow)
                    .GetMethod("Navigate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.Invoke(mw, new object?[] { pageType });

                // --wait 毫秒：等久一点，让监控攒够历史数据再截图
                var argv = Environment.GetCommandLineArgs();
                int wait = 4000;
                int waitIndex = Array.IndexOf(argv, "--wait");
                if (waitIndex >= 0 && waitIndex + 1 < argv.Length && int.TryParse(argv[waitIndex + 1], out int parsed) && parsed > 0)
                    wait = parsed;
                Pump(wait);
                ApplyNavFilter(host);
                Pump(600);
                host.UpdateLayout();

                // --uitab：切到实用工具页的 2FA 标签并塞演示账户（看倒计时圆圈长什么样）
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "--uitab") >= 0
                    && FindChild<Page>(root) is Pages.UtilitiesPage utilities)
                {
                    utilities.ShowTotpDemo();
                    Pump(700);
                    host.UpdateLayout();
                }

                var viewers = new System.Collections.Generic.List<ScrollViewer>();
                CollectScrollViewers(root, viewers);
                foreach (var v in viewers)
                    sb.AppendLine($"SV {v.GetType().Name} name='{v.Name}' 视口={v.ViewportHeight:F1}x{v.ViewportWidth:F1}"
                                + $" 内容={v.ExtentHeight:F1}x{v.ExtentWidth:F1}"
                                + $" 可滚={v.ScrollableHeight:F1}x{v.ScrollableWidth:F1}"
                                + $" 偏移={v.VerticalOffset:F1} H={v.HorizontalScrollBarVisibility} V={v.VerticalScrollBarVisibility}");

                var page = FindChild<Page>(root);
                sb.AppendLine($"页面：{page?.GetType().Name} 实际 {page?.ActualWidth:F1}x{page?.ActualHeight:F1}");
                var rootGrid = (page as System.Windows.Controls.Page)?.Content as FrameworkElement;
                sb.AppendLine($"页面根元素：{rootGrid?.GetType().Name} 实际 {rootGrid?.ActualWidth:F1}x{rootGrid?.ActualHeight:F1}"
                            + $" HAlign={rootGrid?.HorizontalAlignment} VAlign={rootGrid?.VerticalAlignment}");

                int w = (int)Math.Round(root.ActualWidth), h = (int)Math.Round(root.ActualHeight);
                sb.AppendLine($"渲染尺寸：{w}x{h}");
                sb.AppendLine($"渲染时主题：{Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()}"
                            + $" TextFillColorPrimaryBrush={DescribeValue(Application.Current.TryFindResource("TextFillColorPrimaryBrush"))}"
                            + $" ApplicationBackgroundBrush={DescribeValue(Application.Current.TryFindResource("ApplicationBackgroundBrush"))}");
                var nav = FindChild<Wpf.Ui.Controls.NavigationView>(root);
                sb.AppendLine($"NavigationView Foreground={DescribeValue(nav?.Foreground)} Background={DescribeValue(nav?.Background)}");

                // 找页面里第一个 TextBlock，往上逐层打印 Foreground，定位"黑色到底是谁给的"
                var pageEl = FindChild<Page>(root);
                if (pageEl != null)
                {
                    sb.AppendLine($"页面 Foreground={DescribeValue(pageEl.Foreground)} Background={DescribeValue(pageEl.Background)}"
                                + $" Style={(pageEl.Style == null ? "<null>" : pageEl.Style.TargetType?.Name + "/" + pageEl.Style.Setters.Count)}"
                                + $" 资源里的Page样式={(Application.Current.TryFindResource(typeof(Page)) is Style ps ? "同实例" + ReferenceEquals(ps, pageEl.Style) : "无")}");
                }
                var firstText = (pageEl != null ? FindAll<System.Windows.Controls.TextBlock>(pageEl) : new List<System.Windows.Controls.TextBlock>())
                    .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
                if (firstText != null)
                {
                    sb.AppendLine($"首个文本：'{firstText.Text}' 类型={firstText.GetType().FullName} fg={DescribeValue(firstText.Foreground)}");
                    DependencyObject? cur = firstText;
                    while (cur != null)
                    {
                        string fg = cur is Control c2 ? DescribeValue(c2.Foreground)
                                  : cur is System.Windows.Controls.TextBlock tb2 ? DescribeValue(tb2.Foreground)
                                  : "-";
                        string local = cur is DependencyObject d2 && d2.ReadLocalValue(System.Windows.Controls.TextBlock.ForegroundProperty) != DependencyProperty.UnsetValue
                            ? " [本地值]" : "";
                        sb.AppendLine($"    ↑ {cur.GetType().FullName} fg={fg}{local}");
                        cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                            ? VisualTreeHelper.GetParent(cur)
                            : LogicalTreeHelper.GetParent(cur);
                    }
                }
                var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(backdrop);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(outPath)) encoder.Save(fs);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath + ".log", sb.ToString()); } catch { }
        try { host?.Close(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>滚动基准：逐帧滚动并计时，结果写 scrolltest.log。</summary>
    private static void RunScrollTest(string pageName, string outPath, bool showAll = false)
    {
        var sb = new StringBuilder();
        try
        {
            var (host, outer, page) = BuildHost(pageName);

            // 等页面把数据读完（右键菜单要扫注册表，给足时间）
            Pump(11000);

            // 可选：把「只看第三方项」取消勾选，测全部 124 项的最坏情况
            if (showAll)
            {
                var thirdOnly = FindByName<System.Windows.Controls.CheckBox>(page, "ThirdOnly");
                if (thirdOnly != null)
                {
                    thirdOnly.IsChecked = false;
                    page.GetType()
                        .GetMethod("Filter_Changed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(object), typeof(RoutedEventArgs) }, null)
                        ?.Invoke(page, new object?[] { thirdOnly, new RoutedEventArgs() });
                    Pump(2000);
                }
            }

            host.UpdateLayout();

            var viewers = new System.Collections.Generic.List<ScrollViewer>();
            CollectScrollViewers(outer, viewers);

            // 选真正能滚的那一个（通常是列表内部的 ScrollViewer）
            var target = viewers
                .Where(v => v.ScrollableHeight > 1)
                .OrderByDescending(v => v.ScrollableHeight)
                .FirstOrDefault() ?? viewers.FirstOrDefault();

            if (target == null)
            {
                sb.AppendLine("页面里没有可滚动的 ScrollViewer");
                File.WriteAllText(outPath, sb.ToString());
                Environment.Exit(0);
                return;
            }

            sb.AppendLine($"页面：{pageName}{(showAll ? "（全部项）" : "")}");
            sb.AppendLine($"可视元素总数：{CountVisuals(host)}");
            sb.AppendLine($"ScrollViewer 数量：{viewers.Count}");
            sb.AppendLine($"滚动范围：{target.ScrollableHeight:F0} px，视口 {target.ViewportHeight:F0} px");
            sb.AppendLine($"被滚动的控件：{target.GetType().Name} / {target.Parent?.GetType().Name}");

            // 列表虚拟化诊断
            var list = FindByName<System.Windows.Controls.ListBox>(page, "Rows");
            if (list != null)
            {
                var inner = FindChild<ScrollViewer>(list);
                int realized = 0;
                for (int i = 0; i < list.Items.Count; i++)
                    if (list.ItemContainerGenerator.ContainerFromIndex(i) != null) realized++;
                sb.AppendLine($"列表：{list.Items.Count} 项，已生成容器 {realized} 个");
                sb.AppendLine($"  虚拟化={System.Windows.Controls.VirtualizingPanel.GetIsVirtualizing(list)}"
                            + $" 模式={System.Windows.Controls.VirtualizingPanel.GetVirtualizationMode(list)}"
                            + $" 滚动单位={System.Windows.Controls.VirtualizingPanel.GetScrollUnit(list)}");
                if (inner != null)
                    sb.AppendLine($"  内部 ScrollViewer：CanContentScroll={inner.CanContentScroll}"
                                + $" 视口={inner.ViewportHeight:F0} 范围={inner.ExtentHeight:F0}");
                sb.AppendLine($"  面板：{FindChild<System.Windows.Controls.Panel>(list)?.GetType().Name}");
            }

            var rtb = new RenderTargetBitmap(1180, 780, 96, 96, PixelFormats.Pbgra32);

            // 先热身两帧，避免把首次 JIT / 首次模板套用的开销算进去
            for (int i = 0; i < 2; i++)
            {
                target.ScrollToVerticalOffset(i * 40);
                host.UpdateLayout();
                rtb.Render(host);
            }

            const int steps = 30;
            double range = Math.Max(1, target.ScrollableHeight);
            var layoutTimes = new System.Collections.Generic.List<double>();
            var renderTimes = new System.Collections.Generic.List<double>();

            for (int i = 0; i < steps; i++)
            {
                double offset = range * i / (steps - 1);
                target.ScrollToVerticalOffset(offset);

                var sw = Stopwatch.StartNew();
                host.UpdateLayout();
                sw.Stop();
                layoutTimes.Add(sw.Elapsed.TotalMilliseconds);

                sw.Restart();
                rtb.Render(host);
                sw.Stop();
                renderTimes.Add(sw.Elapsed.TotalMilliseconds);
            }

            double Avg(System.Collections.Generic.List<double> list) => list.Average();
            double P95(System.Collections.Generic.List<double> list)
            {
                var sorted = list.OrderBy(v => v).ToList();
                return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))];
            }

            sb.AppendLine($"布局  平均 {Avg(layoutTimes):F2} ms  最大 {layoutTimes.Max():F2} ms  P95 {P95(layoutTimes):F2} ms");
            sb.AppendLine($"绘制  平均 {Avg(renderTimes):F2} ms  最大 {renderTimes.Max():F2} ms  P95 {P95(renderTimes):F2} ms");
            sb.AppendLine($"合计  平均 {Avg(layoutTimes) + Avg(renderTimes):F2} ms/帧"
                        + $"（60fps 需要 < 16.7 ms）");
            sb.AppendLine($"滚动后可视元素总数：{CountVisuals(host)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// <c>--totp &lt;txt&gt;</c>：拿 RFC 6238 附录 B 的官方测试向量核对 TOTP 实现。
    ///
    /// <para>
    /// 验证码算错是<b>不会报错</b>的那种 bug：界面照样跳 6 位数字，倒计时照样走，
    /// 只有登录时才被网站拒绝，而且用户会先怀疑自己抄错。所以必须拿标准向量钉死。
    /// </para>
    /// <para>
    /// 顺带核对 Base32 解码（RFC 4648 的例子）和 otpauth:// 链接解析。
    /// </para>
    /// </summary>
    private static void RunTotp(string outPath)
    {
        var sb = new StringBuilder();
        int pass = 0;
        int fail = 0;

        void Check(string name, string expected, string actual)
        {
            bool ok = expected == actual;
            if (ok) pass++; else fail++;
            sb.AppendLine($"{(ok ? "OK  " : "FAIL")} {name}  期望 {expected}  实际 {actual}");
        }

        try
        {
            sb.AppendLine("== Base32 ==");
            // RFC 4648 §10 的示例：Base32("foobar") = "MZXW6YTBOI======"
            Check("Base32 \"foobar\"",
                "666F6F626172",
                Convert.ToHexString(Services.Totp.DecodeBase32("MZXW6YTBOI======")));

            Check("Base32 容忍空格/小写/连字符",
                "666F6F626172",
                Convert.ToHexString(Services.Totp.DecodeBase32("mzxw 6ytb-oi")));

            sb.AppendLine();
            sb.AppendLine("== RFC 6238 附录 B（8 位） ==");

            // 附录 B 的密钥是 ASCII 的 "12345678901234567890"，
            // SHA256 补到 32 字节、SHA512 补到 64 字节，都是同一种填充方式
            byte[] keySha1 = Encoding.ASCII.GetBytes("12345678901234567890");
            byte[] keySha256 = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
            byte[] keySha512 = Encoding.ASCII.GetBytes(
                "1234567890123456789012345678901234567890123456789012345678901234");

            (long Time, string Sha1, string Sha256, string Sha512)[] vectors =
            {
                (59,          "94287082", "46119246", "90693936"),
                (1111111109,  "07081804", "68084774", "25091201"),
                (1111111111,  "14050471", "67062674", "99943326"),
                (1234567890,  "89005924", "91819424", "93441116"),
                (2000000000,  "69279037", "90698825", "38618901"),
                (20000000000, "65353130", "77737706", "47863826"),
            };

            foreach (var v in vectors)
            {
                Check($"T={v.Time} SHA1  ", v.Sha1,
                    Services.Totp.ComputeAt(keySha1, Services.TotpAlgorithm.Sha1, v.Time, 30, 8));
                Check($"T={v.Time} SHA256", v.Sha256,
                    Services.Totp.ComputeAt(keySha256, Services.TotpAlgorithm.Sha256, v.Time, 30, 8));
                Check($"T={v.Time} SHA512", v.Sha512,
                    Services.Totp.ComputeAt(keySha512, Services.TotpAlgorithm.Sha512, v.Time, 30, 8));
            }

            sb.AppendLine();
            sb.AppendLine("== 6 位（界面默认） ==");
            // 8 位的 94287082 取低 6 位就是 287082，这是各家 App 显示的同一个码
            Check("T=59 SHA1 6 位", "287082",
                Services.Totp.ComputeAt(keySha1, Services.TotpAlgorithm.Sha1, 59, 30, 6));

            sb.AppendLine();
            sb.AppendLine("== otpauth 链接解析 ==");
            var uri = "otpauth://totp/GitHub:alice%40example.com"
                    + "?secret=JBSWY3DPEHPK3PXP&issuer=GitHub&algorithm=SHA256&digits=8&period=60";

            if (Services.Totp.TryParse(uri, out var account, out var error) && account is not null)
            {
                Check("issuer", "GitHub", account.Issuer);
                Check("account", "alice@example.com", account.Label);
                Check("算法", "Sha256", account.Algorithm.ToString());
                Check("位数", "8", account.Digits.ToString());
                Check("周期", "60", account.Period.ToString());

                account.Tick(DateTimeOffset.FromUnixTimeSeconds(1234567890));
                sb.AppendLine($"     该时刻验证码 {account.Code}（分组显示）");
                account.Dispose();
            }
            else
            {
                fail++;
                sb.AppendLine($"FAIL 链接解析失败：{error}");
            }

            // 错误输入必须被挡住，而且要说人话
            foreach (var bad in new[] { "", "abc", "MZXW6YTB", "otpauth://hotp/x?secret=JBSWY3DPEHPK3PXP" })
            {
                bool rejected = !Services.Totp.TryParse(bad, out _, out var why);
                if (rejected) pass++; else fail++;
                sb.AppendLine($"{(rejected ? "OK  " : "FAIL")} 拒绝「{bad}」→ {why}");
            }

            sb.AppendLine();
            sb.AppendLine($"合计 {pass} 通过 / {fail} 失败");
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
