using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace Win11Toolbox.Services;

/// <summary>一个风扇通道：转速 + 当前百分比 + 是否可软件调速。</summary>
public sealed class FanChannel
{
    public string Hardware { get; init; } = "";
    public string Name { get; init; } = "";
    public float? Rpm { get; set; }
    public float? Percent { get; set; }
    public bool Controllable { get; set; }
    public float MinPercent { get; set; }
    public float MaxPercent { get; set; } = 100;

    /// <summary>LHM 的写入句柄。传感器实例在 Computer 生命周期内是稳定的，所以可以长期持有。</summary>
    internal IControl? Control { get; set; }
}

public sealed record TempReading(string Hardware, string Name, float Value);

/// <summary>
/// 风扇读取与调速（基于 LibreHardwareMonitor）。
///
/// <para>
/// 读取转速 / 温度不需要提权；<b>调速</b>要访问主板 SuperIO / EC 芯片，
/// 必须<b>管理员权限</b> + LHM 自带的内核驱动。能不能调取决于主板芯片型号
/// （Nuvoton NCT67xx、ITE IT86xx 这类支持；很多笔记本走 EC，不支持）。
/// </para>
/// <para>
/// 和 <c>Fan Control</c>（Rem0o）同一个原理：LHM 负责和 SuperIO 打交道，
/// 这里只做「读快照 / 写 PWM 百分比」的薄封装。
/// </para>
/// </summary>
public sealed class FanController : IDisposable
{
    private readonly Computer _computer;
    private readonly object _gate = new();
    private bool _opened;
    private bool _disposed;

    public string? LastError { get; private set; }
    public bool Opened => _opened;

    /// <summary>是否枚举到了主板 SuperIO（风扇/EC）。false 通常意味着 PawnIO 驱动没装。</summary>
    public bool SuperIoDetected { get; private set; }

    /// <summary>CPU 温度是否能读到（需要底层驱动，读不到多半是驱动缺失）。</summary>
    public bool CpuTempAvailable { get; private set; }

    public FanController()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            // 这个开关才是「允许写 SuperIO」的关键，关掉就只能读
            IsControllerEnabled = true,
            IsStorageEnabled = false,
            IsMemoryEnabled = false,
            IsNetworkEnabled = false,
        };
    }

    /// <summary>当前进程是否管理员（调速的前提）。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------ PawnIO 驱动

    /// <summary>
    /// 驱动装没装。判据是服务注册表项 —— 设备本身要管理员才能打开，
    /// 用设备句柄判断会在非管理员时误报「没装」。
    /// </summary>
    public static bool DriverPresent
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 安装包位置：优先用程序目录里现成的（开发时方便替换版本），
    /// 否则把嵌进 exe 的那份释放到临时目录 —— 这样单文件版也能一键装驱动。
    /// </summary>
    public static string? BundledInstaller
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "tools", InstallerName),
                Path.Combine(baseDir, InstallerName),
                Path.Combine(baseDir, "..", "tools", InstallerName),
            };

            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch
                {
                    // 路径非法就试下一个
                }
            }

            return ExtractEmbeddedInstaller();
        }
    }

    private static string? ExtractEmbeddedInstaller()
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "Win11Toolbox");
            var target = Path.Combine(folder, InstallerName);

            if (File.Exists(target) && new FileInfo(target).Length > 0)
                return target;

            using var stream = typeof(FanController).Assembly
                .GetManifestResourceStream(InstallerName);
            if (stream is null) return null;

            Directory.CreateDirectory(folder);
            using var file = File.Create(target);
            stream.CopyTo(file);
            return target;
        }
        catch
        {
            return null;
        }
    }

    public const string InstallerName = "PawnIO_setup.exe";

    /// <summary>官方发布地址。随包分发失败时用它兜底下载。</summary>
    public const string InstallerUrl =
        "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";

    /// <summary>
    /// 启动安装程序但**不等待**：PawnIO 的安装器是带界面的，
    /// 用户要在窗口里点两下；卡在这里等会把整个界面冻住。
    /// </summary>
    public static (bool Ok, string Message) StartInstaller()
    {
        if (DriverPresent)
            return (true, "驱动已经装好了。");

        var installer = BundledInstaller;
        if (installer is null)
            return (false, $"找不到 {InstallerName}。\n\n手动装一次：{InstallerUrl}");

        try
        {
            var info = new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installer) ?? AppContext.BaseDirectory,
                Verb = IsElevated ? null : "runas",
            };

            Process.Start(info);
            return (true, "安装程序已启动，按提示点完就行，装好这里会自动刷新。");
        }
        catch (Exception ex)
        {
            return (false, "启动安装程序失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 装 PawnIO 驱动（命令行用：会等它跑完）。
    /// 界面上别用这个，用 <see cref="StartInstaller"/> + 轮询。
    /// </summary>
    public static (bool Ok, string Message) InstallDriver()
    {
        if (DriverPresent)
            return (true, "驱动已经装好了，重启本程序即可读到主板风扇。");

        var installer = BundledInstaller;
        if (installer is null)
            return (false, $"找不到 {InstallerName}。\n\n"
                         + $"把它放到程序目录下的 tools\\ 里再试，或者手动从官网安装：\n{InstallerUrl}");

        try
        {
            var info = new ProcessStartInfo(installer, "/S")
            {
                UseShellExecute = true,
                Verb = IsElevated ? null : "runas",   // 没提权就让 UAC 弹一下
            };

            using var process = Process.Start(info);
            if (process is null)
                return (false, "安装程序没能启动。");

            process.WaitForExit(120_000);

            if (DriverPresent)
                return (true, "驱动装好了。重启本程序（关掉再打开）就能看到主板风扇。");

            return (false, $"安装程序已运行，但没检测到驱动（退出码 {process.ExitCode}）。\n\n"
                         + "可能是被安全软件拦下，或者需要重启电脑后再试。");
        }
        catch (Exception ex)
        {
            return (false, "安装失败：" + ex.Message);
        }
    }

    public bool Open()
    {
        if (_disposed) return false;
        if (_opened) return true;
        try
        {
            _computer.Open();
            _opened = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>刷新并返回当前所有风扇通道与温度。</summary>
    public (List<FanChannel> Fans, List<TempReading> Temps) Snapshot()
    {
        lock (_gate)
        {
            var fans = new List<FanChannel>();
            var temps = new List<TempReading>();
            if (!Open()) return (fans, temps);

            SuperIoDetected = false;
            CpuTempAvailable = false;

            foreach (var root in _computer.Hardware)
                Visit(root, fans, temps);

            return (fans, temps);
        }
    }

    private void Visit(IHardware hw, List<FanChannel> fans, List<TempReading> temps)
    {
        try { hw.Update(); }
        catch { /* 单个硬件刷新失败不影响其它 */ }

        // 主板节点本身有传感器，或出现 SuperIO 子节点，说明底层驱动（PawnIO）工作正常
        if (hw.HardwareType == HardwareType.SuperIO ||
            (hw.HardwareType == HardwareType.Motherboard && hw.Sensors.Length > 0))
        {
            SuperIoDetected = true;
        }

        if (hw.HardwareType == HardwareType.Cpu &&
            hw.Sensors.Any(s => s.SensorType == SensorType.Temperature && s.Value > 0))
        {
            CpuTempAvailable = true;
        }

        // LHM 里同一个风扇通常有两条同名传感器：Fan（转速）和 Control（百分比）。
        // 按名字合并成一个通道。
        var byName = new Dictionary<string, FanChannel>(StringComparer.OrdinalIgnoreCase);
        FanChannel Get(string name)
        {
            if (!byName.TryGetValue(name, out var channel))
            {
                channel = new FanChannel { Hardware = hw.Name, Name = name };
                byName[name] = channel;
            }
            return channel;
        }

        foreach (var sensor in hw.Sensors)
        {
            switch (sensor.SensorType)
            {
                case SensorType.Fan:
                {
                    var channel = Get(sensor.Name);
                    channel.Rpm = sensor.Value;
                    AttachControl(channel, sensor);
                    break;
                }
                case SensorType.Control:
                {
                    var channel = Get(sensor.Name);
                    channel.Percent = sensor.Value;
                    AttachControl(channel, sensor);
                    break;
                }
                case SensorType.Temperature:
                    temps.Add(new TempReading(hw.Name, sensor.Name, sensor.Value ?? 0));
                    break;
            }
        }

        foreach (var channel in byName.Values)
        {
            if (channel.Rpm is not null || channel.Control is not null)
                fans.Add(channel);
        }

        foreach (var sub in hw.SubHardware)
            Visit(sub, fans, temps);
    }

    private static void AttachControl(FanChannel channel, ISensor sensor)
    {
        if (sensor.Control is null)
            return;

        channel.Control = sensor.Control;
        channel.Controllable = true;
        channel.MinPercent = sensor.Control.MinSoftwareValue;
        channel.MaxPercent = sensor.Control.MaxSoftwareValue;
    }

    /// <summary>诊断用：列出 LHM 枚举到的完整硬件树（看主板 / SuperIO 有没有被识别）。</summary>
    public List<string> HardwareTree()
    {
        lock (_gate)
        {
            return HardwareTreeCore();
        }
    }

    private List<string> HardwareTreeCore()
    {
        var lines = new List<string>();
        if (!Open())
        {
            lines.Add("（未打开：" + LastError + "）");
            return lines;
        }

        void Visit(IHardware hw, int depth)
        {
            try { hw.Update(); }
            catch { }

            var counts = hw.Sensors
                .GroupBy(s => s.SensorType)
                .OrderBy(g => g.Key.ToString())
                .Select(g => $"{g.Key}={g.Count()}");

            lines.Add($"{new string(' ', depth * 2)}- {hw.Name}  [{hw.HardwareType}]  传感器: {string.Join(", ", counts)}");

            foreach (var sub in hw.SubHardware)
                Visit(sub, depth + 1);
        }

        foreach (var root in _computer.Hardware)
            Visit(root, 0);

        return lines;
    }

    /// <summary>把某个风扇设成指定百分比（0-100）。</summary>
    public bool SetPercent(FanChannel channel, float percent)
    {
        if (channel.Control is null)
            return false;

        lock (_gate)
        {
            try
            {
                var value = Math.Clamp(percent, channel.MinPercent, channel.MaxPercent);
                channel.Control.SetSoftware(value);
                channel.Percent = value;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>交回 BIOS / 主板默认控制。</summary>
    public bool SetAuto(FanChannel channel)
    {
        if (channel.Control is null)
            return false;

        lock (_gate)
        {
            try
            {
                channel.Control.SetDefault();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_opened)
                _computer.Close();
        }
        catch
        {
            // 关闭失败无所谓
        }
        _opened = false;
    }
}
