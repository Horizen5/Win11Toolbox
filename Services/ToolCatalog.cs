using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Win11Toolbox.Services;

public sealed record ToolItem(string Name, string Description, string Target);

public sealed record ToolCategory(string Title, IReadOnlyList<ToolItem> Items);

/// <summary>
/// 内置 Windows 工具清单 + 启动逻辑。
///
/// <para>
/// <b>图标走字形（Segoe Fluent Icons），不取 exe 图标。</b>
/// 原来是从 host 进程取真实图标，结果：
/// <list type="bullet">
/// <item>`.msc` 一律返回 `mmc.exe`，十几个工具全共用同一个 MMC 图标 —— 张冠李戴；</item>
/// <item>有些取不到，直接是空白；</item>
/// <item>风格也不统一，16px 的老式彩色图标混在 Win11 界面里很扎眼。</item>
/// </list>
/// 系统「设置」应用里这些条目用的就是线框字形，直接沿用它的语汇，
/// 用 <c>SymbolRegular</c>（名字带尺寸后缀，这里统一用 24 档）。
/// 字形名写错<b>不报错</b>，只是显示成空白，所以 <c>--symbols</c> 把全部成员名列出来了再对着配。
/// </para>
/// </summary>
public static class ToolCatalog
{
    /// <summary>
    /// 每个工具对应的字形。名字即 <see cref="SymbolRegular"/> 的成员名（带 24 后缀）。
    /// 全部用 <c>--symbols</c> 校验过存在，写错的名字不会报错、只会显示空白，所以别手改。
    /// </summary>
    private static readonly Dictionary<string, SymbolRegular> Glyphs = new(StringComparer.Ordinal)
    {
        // 系统管理
        ["任务管理器"] = SymbolRegular.TaskListSquareLtr24,
        ["设备管理器"] = SymbolRegular.Board24,
        ["服务和应用程序"] = SymbolRegular.Settings24,
        ["计算机管理"] = SymbolRegular.Desktop24,
        ["注册表编辑器"] = SymbolRegular.Key24,
        ["组策略编辑器"] = SymbolRegular.Shield24,
        ["任务计划程序"] = SymbolRegular.CalendarClock24,
        ["系统配置"] = SymbolRegular.Wrench24,
        ["事件查看器"] = SymbolRegular.Clipboard24,
        ["系统信息"] = SymbolRegular.Info24,
        ["资源监视器"] = SymbolRegular.DataUsage24,
        ["DirectX 诊断"] = SymbolRegular.Beaker24,

        // 硬件与图形
        ["显示设置"] = SymbolRegular.DualScreen24,
        ["显卡设置"] = SymbolRegular.Video24,
        ["磁盘管理"] = SymbolRegular.HardDrive20,
        ["设备与打印机"] = SymbolRegular.Print24,
        ["声音设置"] = SymbolRegular.Speaker224,
        ["电源与电池"] = SymbolRegular.Power24,
        ["蓝牙与设备"] = SymbolRegular.Bluetooth24,
        ["Windows 更新"] = SymbolRegular.ArrowSync24,

        // 网络
        ["网络连接"] = SymbolRegular.NetworkCheck24,
        ["网络和 Internet"] = SymbolRegular.Globe24,
        ["防火墙"] = SymbolRegular.ShieldCheckmark24,
        ["疑难解答"] = SymbolRegular.WrenchScrewdriver24,
        ["查看 IP 配置"] = SymbolRegular.PlugConnected24,
        ["网络测速"] = SymbolRegular.ChartMultiple24,

        // 磁盘与文件
        ["磁盘清理"] = SymbolRegular.Broom24,
        ["磁盘碎片整理"] = SymbolRegular.ArrowClockwise24,
        ["存储感知"] = SymbolRegular.Storage24,
        ["文件资源管理器"] = SymbolRegular.Folder24,
        ["临时文件夹"] = SymbolRegular.Folder24,
        ["启动文件夹"] = SymbolRegular.FolderArrowUp24,
        ["用户目录"] = SymbolRegular.Home24,

        // 命令行与开发
        ["命令提示符"] = SymbolRegular.WindowConsole20,
        ["Windows 终端"] = SymbolRegular.Window24,
        ["PowerShell"] = SymbolRegular.Code24,
        ["系统环境变量"] = SymbolRegular.Edit24,
        ["已安装应用"] = SymbolRegular.AppsList24,
        ["开发人员设置"] = SymbolRegular.DeveloperBoard24,

        // 附件小程序
        ["计算器"] = SymbolRegular.Calculator24,
        ["记事本"] = SymbolRegular.Note24,
        ["画图"] = SymbolRegular.PaintBrush24,
        ["截图工具"] = SymbolRegular.Screenshot24,
        ["放大镜"] = SymbolRegular.ZoomIn24,
        ["屏幕键盘"] = SymbolRegular.Keyboard24,
        ["字符映射表"] = SymbolRegular.TextFont24,
        ["系统还原"] = SymbolRegular.History24,
    };

    /// <summary>磁贴该用的字形。没有配到就返回 null（调用方自己兜底）。</summary>
    public static SymbolRegular? Glyph(ToolItem item)
        => item is null ? null : Glyphs.TryGetValue(item.Name, out var g) ? g : null;

    /// <summary>
    /// 自检：清单里有没有哪个工具忘了配字形，或者配了一个<b>画不出来</b>的字形。
    ///
    /// <para>
    /// 两种坑都<b>不会报错</b>，只会显示成空白或一个残缺的角标：
    /// </para>
    /// <list type="number">
    /// <item>名字拼错 → <see cref="SymbolRegular"/> 里没这个成员（编译期就会挂，问题不大）；</item>
    /// <item>
    /// 名字对、但码位<b>超出 BMP</b>（≥ U+10000）→ 字体里没有这个位置，画出来是个残缺的角标。
    /// 这个最坑：枚举成员是齐的，<c>--symbols</c> 也查不出来，只有真的画一遍才看得见。
    /// </item>
    /// </list>
    /// <para>
    /// 所以这里除了查"有没有配"，还要查"码位在不在 BMP 内"。实测踩过的：
    /// <c>SettingsCogMultiple24</c>(U+F013D)、<c>HardDrive24</c>(U+F0306)、<c>Prompt24</c>(U+F0631)。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> MissingGlyphs()
    {
        var problems = new List<string>();

        foreach (var item in Categories.SelectMany(c => c.Items))
        {
            if (!Glyphs.TryGetValue(item.Name, out var symbol))
            {
                problems.Add(item.Name + "（没配）");
                continue;
            }

            // 图标字体是 16 位 cmap，码位超过 U+FFFF 的一律画不出来
            var code = (int)symbol;
            if (code > 0xFFFF)
                problems.Add($"{item.Name}（{symbol}=U+{code:X}，超出 BMP）");
        }

        return problems;
    }

    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    public static readonly IReadOnlyList<ToolCategory> Categories = new List<ToolCategory>
    {
        new("系统管理", new List<ToolItem>
        {
            new("任务管理器", "查看进程、性能与启动项", "taskmgr.exe"),
            new("设备管理器", "驱动与硬件设备管理", "devmgmt.msc"),
            new("服务和应用程序", "启停 Windows 服务", "services.msc"),
            new("计算机管理", "磁盘、用户、服务的合集", "compmgmt.msc"),
            new("注册表编辑器", "编辑系统注册表", "regedit.exe"),
            new("组策略编辑器", "系统策略配置（专业版）", "gpedit.msc"),
            new("任务计划程序", "定时任务管理", "taskschd.msc"),
            new("系统配置", "启动项与系统引导设置", "msconfig.exe"),
            new("事件查看器", "系统与应用程序日志", "eventvwr.msc"),
            new("系统信息", "完整的软硬件信息报告", "msinfo32.exe"),
            new("资源监视器", "实时资源占用分析", "resmon.exe"),
            new("DirectX 诊断", "显卡与音频诊断工具", "dxdiag.exe"),
        }),
        new("硬件与图形", new List<ToolItem>
        {
            new("显示设置", "分辨率、缩放、多显示器", "ms-settings:display"),
            new("显卡设置", "高级图形与 GPU 首选项", "ms-settings:display-advancedgraphics"),
            new("磁盘管理", "分区、格式化、盘符", "diskmgmt.msc"),
            new("设备与打印机", "打印机与外设", "ms-settings:printers"),
            new("声音设置", "输出设备与音量", "ms-settings:sound"),
            new("电源与电池", "电源模式与电池使用", "ms-settings:batterysaver"),
            new("蓝牙与设备", "蓝牙、鼠标、键盘", "ms-settings:bluetooth"),
            new("Windows 更新", "检查并安装更新", "ms-settings:windowsupdate"),
        }),
        new("网络", new List<ToolItem>
        {
            new("网络连接", "网卡状态与 IP 配置", "ncpa.cpl"),
            new("网络和 Internet", "代理、DNS、适配器设置", "ms-settings:network"),
            new("防火墙", "Windows 安全中心", "ms-settings:windowsdefender"),
            new("疑难解答", "系统内置的自动修复向导", "ms-settings:troubleshoot"),
            new("查看 IP 配置", "命令行输出 ipconfig /all", "cmd.exe /k ipconfig /all"),
            new("网络测速", "打开浏览器测速", "https://www.speedtest.cn/"),
        }),
        new("磁盘与文件", new List<ToolItem>
        {
            new("磁盘清理", "清理系统临时文件", "cleanmgr.exe"),
            new("磁盘碎片整理", "优化驱动器", "dfrgui.exe"),
            new("存储感知", "自动清理与空间管理", "ms-settings:storagesense"),
            new("文件资源管理器", "打开此电脑", "explorer.exe"),
            new("临时文件夹", "打开 %TEMP%", "shell:temp"),
            new("启动文件夹", "开机自启程序目录", "shell:startup"),
            new("用户目录", "打开当前用户文件夹", "shell:profile"),
        }),
        new("命令行与开发", new List<ToolItem>
        {
            new("命令提示符", "cmd.exe", "cmd.exe"),
            new("Windows 终端", "Windows Terminal", "wt.exe"),
            new("PowerShell", "命令行脚本环境", "powershell.exe"),
            new("系统环境变量", "编辑 PATH 等变量", "rundll32.exe sysdm.cpl,EditEnvironmentVariables"),
            new("已安装应用", "应用与功能列表", "ms-settings:appsfeatures"),
            new("开发人员设置", "开发者模式与终端", "ms-settings:developers"),
        }),
        new("附件小程序", new List<ToolItem>
        {
            new("计算器", "标准 / 科学计算器", "calc.exe"),
            new("记事本", "纯文本编辑", "notepad.exe"),
            new("画图", "图像绘制", "mspaint.exe"),
            new("截图工具", "区域截图与录屏", "ms-screenclip:"),
            new("放大镜", "屏幕放大辅助", "magnify.exe"),
            new("屏幕键盘", "虚拟键盘", "osk.exe"),
            new("字符映射表", "特殊字符查询", "charmap.exe"),
            new("系统还原", "创建 / 还原还原点", "rstrui.exe"),
        }),
    };

    public static int TotalCount => Categories.Sum(c => c.Items.Count);

    /// <summary>用于取图标的真实文件路径。</summary>
    public static string? IconSource(ToolItem item)
    {
        var target = item.Target;
        if (target.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(System32, "mmc.exe");
        if (target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                        "ImmersiveControlPanel", "SystemSettings.exe");
            return File.Exists(settings) ? settings : null;
        }
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        var first = target.Split(' ')[0];
        var path = Path.Combine(System32, first);
        return File.Exists(path) ? path : null;
    }

    public static ImageSource? Icon(ToolItem item)
    {
        var source = IconSource(item);
        return source is null ? null : ShellIcon.FromPath(source);
    }

    /// <summary>
    /// 协议 / URL / Shell 目标：交给 ShellExecute 打开，不按可执行文件去解析。
    /// （ms-settings:、shell:、ms-screenclip:、https://… 都属于这类）
    /// </summary>
    private static bool IsProtocolTarget(string target)
        => target.StartsWith("ms-settings", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
        || target.Contains("://", StringComparison.Ordinal)
        || target.EndsWith(":", StringComparison.Ordinal);

    /// <summary>
    /// 诊断用：把每个工具的目标解析一遍，返回 (工具名, 目标, 结果)。
    /// 用途：找出这台机器上「点了会失败」的工具（如没装 Windows 终端时的 wt.exe、没装画图时的 mspaint.exe）。
    /// </summary>
    internal static IEnumerable<(string Name, string Target, string Status)> ProbeTargets()
    {
        foreach (var item in Categories.SelectMany(c => c.Items))
        {
            var target = item.Target;
            string status;

            if (IsProtocolTarget(target))
            {
                status = "协议/URL（ShellExecute）";
            }
            else if (target.EndsWith(".cpl,EditEnvironmentVariables", StringComparison.OrdinalIgnoreCase))
            {
                status = "OK  rundll32 sysdm.cpl";
            }
            else
            {
                var exeName = target.Contains(' ') ? target.Split(' ', 2)[0] : target;
                var resolved = ResolveExecutable(exeName);
                if (resolved is not null)
                    status = "OK  " + resolved;
                else if (target.Equals("wt.exe", StringComparison.OrdinalIgnoreCase))
                    status = "缺失 wt.exe（会退回命令提示符）";
                else
                    status = "缺失 " + exeName + "（点击会提示未安装）";
            }

            yield return (item.Name, target, status);
        }
    }

    /// <summary>
    /// 启动一个工具。
    /// 返回 <c>null</c> 表示按原样启动了；返回非空字符串表示「没能按原样启动，但有话要跟用户说」
    /// （Windows 终端没装 → 退回命令提示符；其它 exe 缺失 → 友好提示未安装，不再抛英文 Win32 报错）。
    /// </summary>
    public static string? Launch(ToolItem item) => Launch(item.Target, item.Name);

    private static string? Launch(string target, string name)
    {
        if (target.EndsWith(".cpl,EditEnvironmentVariables", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables")
            {
                UseShellExecute = true,
            });
            return null;
        }

        // Windows 终端：wt.exe 是 App Execution Alias（%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe），
        // 很多机器压根没装。直接 Process.Start("wt.exe") 会抛「系统找不到指定的文件」，所以单独处理。
        if (target.Equals("wt.exe", StringComparison.OrdinalIgnoreCase))
            return LaunchWindowsTerminal();

        // 协议 / URL / shell: —— 直接交给 ShellExecute
        if (IsProtocolTarget(target))
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }

        // 带参数的命令：cmd.exe /k ipconfig /all
        if (target.Contains(' '))
        {
            var parts = target.Split(' ', 2);
            var exePath = ResolveExecutable(parts[0]);
            if (exePath is null)
                return Missing(name, parts[0]);
            Process.Start(new ProcessStartInfo(exePath, parts[1]) { UseShellExecute = true });
            return null;
        }

        // 单个可执行文件：别名 / PATH / System32 里找，找不到就友好提示
        var resolved = ResolveExecutable(target);
        if (resolved is null)
            return Missing(name, target);

        Process.Start(new ProcessStartInfo(resolved) { UseShellExecute = true });
        return null;
    }

    /// <summary>本机没装这个程序时的友好提示，替掉 .NET 那句「An error occurred trying to start process…」。</summary>
    private static string Missing(string name, string exe)
        => $"这台电脑没有安装「{name}」（找不到 {exe}），无法打开。\n\n"
         + "如果这是 Windows 自带功能，可以在「设置 → 应用 → 可选功能」或 Microsoft Store 里安装后再试。";

    /// <summary>
    /// Windows 终端：先找 wt.exe（App 别名 / PATH / System32），找到就开；
    /// 找不到说明没装，退回命令提示符，并把原因返回给页面去提示。
    /// </summary>
    private static string? LaunchWindowsTerminal()
    {
        var wt = ResolveExecutable("wt.exe");
        if (wt is not null)
        {
            Process.Start(new ProcessStartInfo(wt) { UseShellExecute = true });
            return null;
        }

        // 没装 Windows Terminal → 至少给个能用的终端，并说清为什么
        var cmd = Path.Combine(System32, "cmd.exe");
        Process.Start(new ProcessStartInfo(File.Exists(cmd) ? cmd : "cmd.exe") { UseShellExecute = true });
        return "这台电脑没有安装「Windows 终端」（找不到 wt.exe），已经用「命令提示符」代替打开。\n\n"
             + "想用真正的 Windows 终端，可以在 Microsoft Store 搜索「Windows 终端」安装；装好之后这里会自动改用终端。";
    }

    /// <summary>
    /// 找一个可执行文件：先看 App Execution Alias（%LOCALAPPDATA%\Microsoft\WindowsApps），
    /// 再扫 PATH，最后看 System32。都没有返回 <c>null</c>。
    /// </summary>
    private static string? ResolveExecutable(string exeName)
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", exeName);
        if (File.Exists(alias))
            return alias;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // 路径非法就跳过
            }
        }

        var system32 = Path.Combine(System32, exeName);
        return File.Exists(system32) ? system32 : null;
    }
}
