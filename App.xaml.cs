using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Win11Toolbox;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win11Toolbox", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        Services.StartupTrace.Mark("OnStartup 进入");
        base.OnStartup(e);
        Services.StartupTrace.Mark("base.OnStartup 完成");

        // 单文件发布下 WPF 的关闭遥测会 Assembly.Load("System.Diagnostics.Tracing, 10.0.0.0, …")，
        // 这个程序集要是还没被加载过，解析就失败、抛 FileNotFoundException —— 而且是在
        // Application.CriticalShutdown 里抛的，直接把关闭流程带崩：窗口没了、进程不退，
        // 单实例锁一直攥着，用户之后再双击就完全没反应（2026-11-01 就是这么卡住的）。
        // 这里先摸一下，保证它已经在加载器里，后面按全名 Load 才找得到。
        _ = typeof(System.Diagnostics.Tracing.EventSource).Assembly;

        // 主题要先切，后面的离屏诊断（--navshot / --shot）也才能按 --theme 出图
        ApplyThemeFromArgs();
        Services.StartupTrace.Mark("主题已应用");

        // --probe：纯后台探测，不建任何窗口，跑完直接退出（写 probe.log）
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--probe") >= 0)
        {
            RunProbe();
            return;
        }

        // --scrolltest <页面> [log]：离屏滚一遍并计时，结果写 scrolltest.log
        var scrollIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--scrolltest");
        if (scrollIndex >= 0 && scrollIndex + 1 < Environment.GetCommandLineArgs().Length)
        {
            var a = Environment.GetCommandLineArgs();
            string log = scrollIndex + 2 < a.Length
                ? a[scrollIndex + 2]
                : Path.Combine(AppContext.BaseDirectory, "scrolltest.log");
            bool showAll = Array.IndexOf(a, "--all") >= 0;
            RunScrollTest(a[scrollIndex + 1], log, showAll);
            return;
        }

        // --shot <页面> <png>：把页面渲染成图片做视觉检查。
        // 窗口放在屏幕外、不激活、不进任务栏，所以不会打断正在玩的游戏。
        var shotIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--shot");
        if (shotIndex >= 0 && shotIndex + 2 < Environment.GetCommandLineArgs().Length)
        {
            var a = Environment.GetCommandLineArgs();
            RunShot(a[shotIndex + 1], a[shotIndex + 2]);
            return;
        }

        // --navshot <页面> <png>：用真实 NavigationView 离屏渲染，验证外层 ScrollViewer 有没有切掉标题
        var navIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--navshot");
        if (navIndex >= 0 && navIndex + 2 < Environment.GetCommandLineArgs().Length)
        {
            var a = Environment.GetCommandLineArgs();
            RunNavShot(a[navIndex + 1], a[navIndex + 2]);
            return;
        }

        // --pxinfo <png> <行> <列> <txt>：量截图里的像素色块，排查"那条线到底是什么"
        var pxIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--pxinfo");
        if (pxIndex >= 0 && pxIndex + 4 < Environment.GetCommandLineArgs().Length)
        {
            var a = Environment.GetCommandLineArgs();
            RunPixelInfo(a[pxIndex + 1],
                         int.TryParse(a[pxIndex + 2], out int r) ? r : -1,
                         int.TryParse(a[pxIndex + 3], out int c) ? c : -1,
                         a[pxIndex + 4]);
            return;
        }

        // --logtest <txt>：从后台线程调 Log()，验证跨线程写入被正确切回 UI 线程
        var logTestIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--logtest");
        if (logTestIndex >= 0 && logTestIndex + 1 < Environment.GetCommandLineArgs().Length)
        {
            RunLogTest(Environment.GetCommandLineArgs()[logTestIndex + 1]);
            return;
        }

        // --navdump <页面> <txt>：打印焦点广告层 + 左侧导航视觉树（排查导航栏上的框）
        var dumpIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--navdump");
        if (dumpIndex >= 0 && dumpIndex + 2 < Environment.GetCommandLineArgs().Length)
        {
            var a = Environment.GetCommandLineArgs();
            RunNavDump(a[dumpIndex + 1], a[dumpIndex + 2]);
            return;
        }

        // --themedump <txt>：打印主题字典合并情况 + 关键画笔解析值（排查深色模式发黑）
        var themeDumpIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--themedump");
        if (themeDumpIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = themeDumpIndex + 1 < a.Length
                ? a[themeDumpIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "themedump.log");
            RunThemeDump(log);
            return;
        }

        // --rowdump <页面> <txt>：在真窗口上量列表行文字的 Foreground（排查"行里字是黑的"）
        var rowIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--rowdump");
        if (rowIndex >= 0 && rowIndex + 2 < Environment.GetCommandLineArgs().Length)
        {
            var a = Environment.GetCommandLineArgs();
            RunRowDump(a[rowIndex + 1], a[rowIndex + 2]);
            return;
        }

        // --darkaudit <txt>：深色主题下逐页扫"偏暗文字"，防止还有黑字漏网
        var auditIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--darkaudit");
        if (auditIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = auditIndex + 1 < a.Length && !a[auditIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[auditIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "darkaudit.log");
            RunDarkAudit(log);
            return;
        }

        // --themeflash <txt>：逐帧记录切换深浅色时的画面状态（查"切换时闪一下"）
        var flashIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--themeflash");
        if (flashIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = flashIndex + 1 < a.Length && !a[flashIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[flashIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "themeflash.log");
            RunThemeFlash(log);
            return;
        }

        // --symbols：列出 SymbolRegular 全部成员名（给工具磁贴配字形用）
        // --glyphsheet <png>：把 47 个工具图标画成一张总表，看谁的字形是残缺的
        var sheetIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--glyphsheet");
        if (sheetIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string png = sheetIndex + 1 < a.Length && !a[sheetIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[sheetIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "glyphsheet.png");
            RunGlyphSheet(png);
            return;
        }

        // --glyphaudit <txt> [关键词…]：查字形码位在本机 Segoe Fluent Icons 里是否真有字形
        var glyphIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--glyphaudit");
        if (glyphIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = glyphIndex + 1 < a.Length && !a[glyphIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[glyphIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "glyphaudit.log");
            RunGlyphAudit(log, a.Skip(glyphIndex + 2).Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToArray());
            return;
        }

        // --ringsheet <png>：把 2FA 倒计时圆圈各档进度排成一行，核对绿→黄→红
        var ringIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--ringsheet");
        if (ringIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string png = ringIndex + 1 < a.Length && !a[ringIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[ringIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "ringsheet.png");
            RunRingSheet(png);
            return;
        }

        // --setsb <0|1|2> <log>：设置任务栏搜索框模式（0=隐藏 1=仅图标 2=搜索框），用于实测是否免重启生效
        var setSbIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--setsb");
        if (setSbIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            int mode = 0;
            int.TryParse(setSbIndex + 1 < a.Length ? a[setSbIndex + 1] : "0", out mode);
            string log = setSbIndex + 2 < a.Length && !a[setSbIndex + 2].StartsWith("--", StringComparison.Ordinal)
                ? a[setSbIndex + 2]
                : Path.Combine(AppContext.BaseDirectory, "setsb.log");
            string res = SettingsKit.EasySettingsData.SetTaskbarSearchMode(mode, out string serr)
                ? "OK" : "FAIL " + serr;
            try { File.WriteAllText(log, res, Encoding.UTF8); } catch { }
            Environment.Exit(0);
        }

        // --totp <txt>：用 RFC 6238 官方测试向量核对 TOTP 实现（含 Base32 与 otpauth 解析）
        var totpIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--totp");
        if (totpIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = totpIndex + 1 < a.Length && !a[totpIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[totpIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "totp.log");
            RunTotp(log);
            return;
        }

        var symbolsIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--symbols");
        if (symbolsIndex >= 0)
        {
            string log = symbolsIndex + 1 < Environment.GetCommandLineArgs().Length
                && !Environment.GetCommandLineArgs()[symbolsIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? Environment.GetCommandLineArgs()[symbolsIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "symbols.log");
            RunSymbols(log);
            return;
        }

        // --dialogdump <txt>：量重命名对话框的上/下留白差多少
        var dialogIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--dialogdump");
        if (dialogIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = dialogIndex + 1 < a.Length && !a[dialogIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[dialogIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "dialogdump.log");
            RunDialogDump(log);
            return;
        }

        // --switchtest <txt>：点一下深浅色开关，采样"主题什么时候变 / 蒙层什么时候出现"
        var switchIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--switchtest");
        if (switchIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = switchIndex + 1 < a.Length && !a[switchIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[switchIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "switchtest.log");
            RunSwitchTest(log);
            return;
        }

        // --toggleanim <txt>：把 ToggleSwitch 模板里的动画定义全挖出来（查"开关滑得不丝滑"）
        var toggleIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--toggleanim");
        if (toggleIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = toggleIndex + 1 < a.Length && !a[toggleIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[toggleIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "toggleanim.log");
            RunToggleAnim(log);
            return;
        }

        // --appdump <txt>：把「应用管理」依赖的 winget / Appx 查询结果落盘，
        //                  验证 tab 分隔解析、预装包版本段提取、ID 匹配
        var appIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--appdump");
        if (appIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = appIndex + 1 < a.Length && !a[appIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[appIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "appdump.log");
            RunAppDump(log);
            Environment.Exit(0);
        }

        // --powerdump <txt>：把 powercfg /list 读出来的电源计划名原样写文件，
        //                    专门验证 OEM 代码页编码修复后中文名不再乱码
        var powerIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--powerdump");
        if (powerIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = powerIndex + 1 < a.Length && !a[powerIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[powerIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "powerdump.log");
            RunPowerDump(log);
            return;
        }

        // --launchprobe <txt>：把每个工具的目标解析一遍，列出本机不可用的（如没装终端时的 wt.exe）
        var launchIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--launchprobe");
        if (launchIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = launchIndex + 1 < a.Length && !a[launchIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[launchIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "launchprobe.log");
            RunLaunchProbe(log);
            return;
        }

        // --monitorcost <txt>：量主页每秒采样各步骤的耗时
        var costIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--monitorcost");
        if (costIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = costIndex + 1 < a.Length && !a[costIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[costIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "monitorcost.log");
            RunMonitorCost(log);
            return;
        }

        // --startupicons <txt>：逐个启动项验证 exe/lnk 解析 + 图标提取
        var startupIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--startupicons");
        if (startupIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = startupIndex + 1 < a.Length && !a[startupIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[startupIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "startupicons.log");
            RunStartupIcons(log);
            return;
        }

        // --fans <txt>：扫一遍风扇/温度，确认能否读取/调速
        var fansIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--fans");
        if (fansIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            string log = fansIndex + 1 < a.Length && !a[fansIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[fansIndex + 1]
                : Path.Combine(AppContext.BaseDirectory, "fans.log");
            RunFans(log);
            return;
        }

        // --installpawnio <txt>：走界面按钮同一条路径装驱动
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--installpawnio") >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(a, "--installpawnio");
            var log = index + 1 < a.Length && !a[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? a[index + 1]
                : Path.Combine(AppContext.BaseDirectory, "installpawnio.log");
            RunInstallPawnIo(log);
            return;
        }

        // --adlfan [百分比] <txt>：直接调 ADL OverdriveN 试写显卡风扇
        var adlIndex = Array.IndexOf(Environment.GetCommandLineArgs(), "--adlfan");
        if (adlIndex >= 0)
        {
            var a = Environment.GetCommandLineArgs();
            var percent = 50;
            string log = Path.Combine(AppContext.BaseDirectory, "adlfan.log");

            for (var i = adlIndex + 1; i < a.Length; i++)
            {
                if (int.TryParse(a[i], out var value)) percent = Math.Clamp(value, 0, 100);
                else if (!a[i].StartsWith("--", StringComparison.Ordinal)) log = a[i];
            }

            RunAdlFan(log, percent);
            return;
        }

        // --writetest：挑一个没在运行的托盘项，写 1 → 读回 → 写 0 → 读回，
        //              验证写权限和写后生效，全程不碰正在用的图标
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--writetest") >= 0)
        {
            RunWriteTest();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        // 单实例：已经在跑了就把那个窗口唤到前台，自己安静退出。
        // 两个实例同时写 disabled.tsv / backups 会把「已禁用」记录和备份搞乱。
        // 上面的诊断模式都在这之前 return 了，所以可以和应用同时开。
        //
        // --selftest / --easytest 例外：它们要建真正的 MainWindow，不能像别的诊断那样提前 return；
        // 但也不能被单实例挡住 —— 否则用户开着应用时跑自检会「静默退出码 0、却没写日志」，
        // 看起来像通过、其实是没跑（这个假阳性坑过一次，--easytest 后来也踩了同一个坑）。
        bool isSelfTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--selftest") >= 0
                       || Array.IndexOf(Environment.GetCommandLineArgs(), "--uistall") >= 0
                       || Array.IndexOf(Environment.GetCommandLineArgs(), "--easytest") >= 0;
        Services.StartupTrace.Mark("诊断参数判断完成");
        if (!isSelfTest)
        {
            Services.StartupTrace.Mark("单实例检查开始");
            if (!Services.SingleInstance.TryAcquire())
            {
                Shutdown();
                return;
            }
            Services.StartupTrace.Mark("单实例通过");

            Exit += (_, _) => Services.SingleInstance.Release();
        }

        Services.StartupTrace.StartStallProbe(Dispatcher);
        Services.StartupTrace.Mark("OnStartup 结束（下面开始建窗口）");
    }

    /// <summary>离屏渲染一个页面，存成 PNG。全程不显示窗口、不抢焦点。</summary>
    private static void RunShot(string pageName, string outPath)
    {
        // 和 --scrolltest 用同一个宿主，保证截出来的排版就是真实导航里的排版
        var (host, outer, _) = BuildHost(pageName);

        // 默认等页面把数据读完（右键菜单要扫注册表，给足时间）。
        // --pump N 改成只等 N 毫秒 —— 专门用来量"首屏到底空不空"：
        // 渲染得早还能看到内容，说明页面不是等数据全部读完才肯画的。
        var argv = Environment.GetCommandLineArgs();
        int pumpMs = 12000;
        int pumpIndex = Array.IndexOf(argv, "--pump");
        if (pumpIndex >= 0 && pumpIndex + 1 < argv.Length
            && int.TryParse(argv[pumpIndex + 1], out int pumpArg) && pumpArg >= 0)
            pumpMs = pumpArg;

        Pump(pumpMs);
        host.UpdateLayout();

        try
        {
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                1180, 780, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(outer);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath)) encoder.Save(fs);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPath + ".err", ex.ToString()); } catch { }
        }
        Environment.Exit(0);
    }

    /// <summary>验证托盘开关的写入路径：改一个没在运行的项，读回确认，再改回去。</summary>
    private static void RunWriteTest()
    {
        var sb = new StringBuilder();
        try
        {
            var list = Win11Toolbox.Tray.TraySettings.ReadEntries(sb);
            Win11Toolbox.Tray.TraySettings.Entry? target = null;
            foreach (var en in list)
            {
                string stem;
                try { stem = Path.GetFileNameWithoutExtension(en.ExePath); } catch { stem = ""; }
                if (stem.Length == 0) continue;
                bool running = false;
                try { running = System.Diagnostics.Process.GetProcessesByName(stem).Length > 0; }
                catch { }
                if (running) continue;           // 只动没在运行的，避免任务栏图标跳动
                target = en;
                break;
            }

            if (target == null)
            {
                sb.AppendLine("找不到可安全测试的条目");
            }
            else
            {
                sb.AppendLine($"测试目标：{target.Name}  当前 {(target.On ? "始终显示" : "溢出区")}  键 {target.Key}");

                var on = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, bool>>
                { new System.Collections.Generic.KeyValuePair<string, bool>(target.Key, true) };
                var off = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, bool>>
                { new System.Collections.Generic.KeyValuePair<string, bool>(target.Key, false) };

                sb.AppendLine("写入 1 → " + Win11Toolbox.Tray.TraySettings.Apply(on, sb));
                sb.AppendLine("读回： " + StateOf(target.Key));

                sb.AppendLine("写入 0 → " + Win11Toolbox.Tray.TraySettings.Apply(off, sb));
                sb.AppendLine("读回： " + StateOf(target.Key));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "writetest.log"), sb.ToString()); }
        catch { }
        Environment.Exit(0);
    }

    private static string StateOf(string key)
    {
        foreach (var en in Win11Toolbox.Tray.TraySettings.ReadEntries(null))
            if (en.Key == key) return en.On ? "始终显示（IsPromoted=1）" : "溢出区（IsPromoted=0）";
        return "没读到";
    }

    /// <summary>不建窗口地跑一遍托盘模块，结果写 probe.log，然后退出。</summary>
    private static void RunProbe()
    {
        var sb = new StringBuilder();
        try
        {
            var list = Win11Toolbox.Tray.TraySettings.ReadEntries(sb);
            sb.AppendLine($"清单条目：{list.Count} 项");
            foreach (var en in list.Take(14))
                sb.AppendLine($"  [{(en.On ? "任务栏" : "溢出区")}] {en.Name,-28} {en.ExePath}");

            var rows = Win11Toolbox.Tray.TrayCore.Load(sb);
            sb.AppendLine($"合并行数：{rows.Count}，正在运行：{rows.Count(r => r.Live)}，"
                        + $"有清单键：{rows.Count(r => r.SettingsKey.Length > 0)}，"
                        + $"固定在任务栏：{rows.Count(r => r.SettingsState == 1)}");
            foreach (var r in rows.Take(60))
                sb.AppendLine($"  · {r.AppName,-26} 状态={r.SettingsState,2} 在托盘={(r.Live ? "是" : "否")} "
                            + $"图标={(r.Icon != null ? "有" : "无")} {r.ExePath}");

            // 改名的名字校验：只跑规则，不动系统
            var cases = new (string Name, bool Expect)[]
            {
                ("HORIZEN", true), ("My-PC", true), ("a", true),
                ("", false), ("abcdefghijklmnop", false), ("123456", false),
                ("-abc", false), ("abc-", false), ("我的电脑", false), ("a b", false),
            };
            int bad = 0;
            foreach (var (name, expect) in cases)
            {
                bool ok = Win11Toolbox.Services.ComputerRename.IsValid(name, out _);
                if (ok != expect) bad++;
            }
            sb.AppendLine($"名字校验：{cases.Length} 个用例，{(bad == 0 ? "全部符合预期" : bad + " 个不符合预期")}");

            // 三个新页面的 XAML 能否正常解析（不显示窗口，只构造）
            foreach (var t in new[]
            {
                typeof(Win11Toolbox.Pages.RightMenuPage),
                typeof(Win11Toolbox.Pages.TrayIconsPage),
                typeof(Win11Toolbox.Pages.EasySettingsPage),
            })
            {
                try
                {
                    var page = (FrameworkElement)Activator.CreateInstance(t)!;
                    sb.AppendLine($"页面 {t.Name}：构造成功");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"页面 {t.Name}：构造失败 {ex.GetType().Name} {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("异常：" + ex);
        }

        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe.log"), sb.ToString()); }
        catch { }
        Environment.Exit(0);
    }

    /// <summary>
    /// 主题：命令行 --theme 优先（截图/验证用），否则读配置里记住的那个，都没有就跟系统。
    /// </summary>
    private static void ApplyThemeFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--theme");

        string theme;
        if (index >= 0 && index + 1 < args.Length)
            theme = args[index + 1].ToLowerInvariant();
        else
            theme = Services.AppConfig.Current.Theme;

        switch (theme)
        {
            case "dark":
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                break;
            case "light":
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                break;
            default:
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }

    /// <summary>单个操作出错时记日志并提示，不让整个程序闪退。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);

        try
        {
            MessageBox.Show($"操作失败：{e.Exception.Message}\n\n详细信息已记录到：\n{LogPath}",
                "Win11 工具箱", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // 提示框本身失败就算了
        }

        e.Handled = true;
    }

    internal static void Log(Exception? exception)
    {
        if (exception is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var builder = new StringBuilder();
            builder.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            builder.AppendLine(exception.ToString());
            builder.AppendLine();
            File.AppendAllText(LogPath, builder.ToString());
        }
        catch
        {
            // 日志写不进去也不能再抛异常
        }
    }
}
