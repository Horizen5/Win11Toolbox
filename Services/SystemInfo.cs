using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;

namespace Win11Toolbox.Services;

public sealed class MemoryModule
{
    public string Slot = "—", Size = "—", Speed = "—", Type = "—", Vendor = "—", Part = "—";
}

public sealed class DiskInfo
{
    public string Model = "—", Size = "—", Type = "—", Health = "—", Serial = "—";
}

public sealed class VolumeInfo
{
    public string Mount = "—", FileSystem = "—", Total = "—", Used = "—", Free = "—", PercentText = "—";
    public double Percent;
}

public sealed class NetworkInfo
{
    public string Name = "—", Ip = "—", Mac = "—", Gateway = "—", Dns = "—", Assign = "—";
}

public sealed class HardwareReport
{
    public List<InfoItem> Overview { get; set; } = new();
    public List<InfoItem> Cpu { get; set; } = new();
    public List<InfoItem> Memory { get; set; } = new();
    public List<MemoryModule> Modules { get; set; } = new();
    public List<GpuBlock> Gpus { get; set; } = new();
    public List<InfoItem> Board { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = new();
    public List<VolumeInfo> Volumes { get; set; } = new();
    public List<NetworkInfo> Networks { get; set; } = new();
    public List<InfoItem> Other { get; set; } = new();

    /// <summary>采集完成的时间，用于判断缓存是否过期。</summary>
    public DateTime CollectedAt { get; set; } = DateTime.Now;
}

/// <summary>通过 WMI 读取硬件信息，一次采集、结果缓存。</summary>
public static class SystemInfo
{
    private static readonly Dictionary<int, string> MemoryTypes = new()
    {
        { 20, "DDR" }, { 21, "DDR2" }, { 22, "DDR2" }, { 24, "DDR3" },
        { 26, "DDR4" }, { 27, "DDR4" }, { 34, "DDR5" }, { 35, "DDR5" },
    };

    public static HardwareReport Build()
    {
        var report = new HardwareReport();
        var os = First("SELECT * FROM Win32_OperatingSystem");
        var cs = First("SELECT * FROM Win32_ComputerSystem");
        var cpu = First("SELECT * FROM Win32_Processor");
        var board = First("SELECT * FROM Win32_BaseBoard");
        var bios = First("SELECT * FROM Win32_BIOS");
        var modules = All("SELECT * FROM Win32_PhysicalMemory");
        var gpus = All("SELECT * FROM Win32_VideoController");
        var disks = All("SELECT * FROM Win32_DiskDrive");
        var physical = All("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
        var nets = All("SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
        var batteries = All("SELECT * FROM Win32_Battery");
        var sounds = All("SELECT * FROM Win32_SoundDevice");
        var monitors = All("SELECT * FROM WmiMonitorID", @"root\wmi");
        var tpm = First("SELECT * FROM Win32_Tpm", @"root\cimv2\security\microsofttpm");

        // 虚拟显示器（Oray / ToDesk 之类）排到后面，主显卡优先
        string[] virtualKeys = { "oray", "idddriver", "virtual", "vmware", "virtualbox", "parsec",
                                 "splashtop", "citrix", "basic display", "todesk", "sunlogin",
                                 "anydesk", "gameviewer", "radmin", "usb display", "spacedesk" };
        var realGpus = gpus.Where(g => !virtualKeys.Any(k => Str(g, "Name").ToLower().Contains(k))).ToList();
        var allGpus = realGpus.Concat(gpus.Except(realGpus)).ToList();
        var primaryGpus = realGpus.Count > 0 ? realGpus : allGpus;

        var memoryTotal = modules.Aggregate(0UL, (total, m) => total + (ulong)Num(m, "Capacity"));

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var uptimeText = $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时 {uptime.Minutes} 分";

        var overview = new List<InfoItem>
        {
            new("设备名称", Str(cs, "Name")),
            new("设备型号", $"{Str(cs, "Manufacturer")} {Str(cs, "Model")}".Trim()),
            new("操作系统", Str(os, "Caption")),
            new("系统版本", $"{Str(os, "Version")} (Build {Str(os, "BuildNumber")})"),
            new("系统架构", Str(os, "OSArchitecture")),
            new("处理器", Str(cpu, "Name").Trim()),
            new("内存容量", ByteFormatter.Format(memoryTotal, 0)),
            new("显卡", string.Join(" / ", primaryGpus.Take(2).Select(g => Str(g, "Name")))),
            new("主板", $"{Str(board, "Manufacturer")} {Str(board, "Product")}".Trim()),
            new("系统安装时间", Date(os, "InstallDate")),
            new("已运行时间", uptimeText),
            new("当前用户", Str(cs, "UserName")),
        };
        report.Overview = overview;

        report.Cpu = new List<InfoItem>
        {
            new("处理器型号", Str(cpu, "Name").Trim()),
            new("核心 / 线程", $"{Num(cpu, "NumberOfCores")} 核 / {Num(cpu, "NumberOfLogicalProcessors")} 线程"),
            new("基准频率", Num(cpu, "MaxClockSpeed") > 0 ? $"{Num(cpu, "MaxClockSpeed")} MHz" : "—"),
            new("二级缓存", Num(cpu, "L2CacheSize") > 0 ? $"{Num(cpu, "L2CacheSize")} KB" : "—"),
            new("三级缓存", Num(cpu, "L3CacheSize") > 0 ? $"{Num(cpu, "L3CacheSize")} KB" : "—"),
            new("插槽", Str(cpu, "SocketDesignation")),
            new("指令集位宽", $"{Num(cpu, "AddressWidth")} 位"),
            new("虚拟化（固件）", Str(cpu, "VirtualizationFirmwareEnabled") is "True" ? "已启用" : "未启用 / 未知"),
            new("处理器 ID", Str(cpu, "ProcessorId")),
        };

        var (used, total, _) = NativeMethods.Memory();
        report.Memory = new List<InfoItem>
        {
            new("总容量", ByteFormatter.Format(memoryTotal, 0)),
            new("插槽数量", $"{modules.Count} 条"),
            new("内存类型", modules.Count > 0 ? MemoryTypes.GetValueOrDefault((int)Num(modules[0], "SMBIOSMemoryType"), "—") : "—"),
            new("当前频率", modules.Count > 0 && Num(modules[0], "Speed") > 0 ? $"{Num(modules[0], "Speed")} MHz" : "—"),
            new("已用 / 可用", $"{ByteFormatter.Format(used)} 已用 / {ByteFormatter.Format(total - used)} 可用"),
        };

        foreach (var module in modules)
        {
            report.Modules.Add(new MemoryModule
            {
                Slot = Str(module, "DeviceLocator"),
                Size = ByteFormatter.Format((ulong)Num(module, "Capacity"), 0),
                Speed = Num(module, "Speed") > 0 ? $"{Num(module, "Speed")} MHz" : "—",
                Type = MemoryTypes.GetValueOrDefault((int)Num(module, "SMBIOSMemoryType"), "—"),
                Vendor = Str(module, "Manufacturer"),
                Part = Str(module, "PartNumber").Trim(),
            });
        }

        foreach (var gpu in allGpus)
        {
            var name = Str(gpu, "Name");
            var title = realGpus.Contains(gpu) ? name : $"{name}（虚拟 / 远程显示适配器）";
            var rows = new List<InfoItem>
            {
                new("显卡名称", title),
                new("显存（报告值）", ByteFormatter.Format((ulong)Num(gpu, "AdapterRAM"))),
                new("驱动版本", Str(gpu, "DriverVersion")),
                new("驱动日期", Date(gpu, "DriverDate")),
                new("当前分辨率", $"{Num(gpu, "CurrentHorizontalResolution")}×{Num(gpu, "CurrentVerticalResolution")}"),
                new("刷新率", Num(gpu, "CurrentRefreshRate") > 0 ? $"{Num(gpu, "CurrentRefreshRate")} Hz" : "—"),
                new("厂商", Str(gpu, "AdapterCompatibility")),
            };
            report.Gpus.Add(new GpuBlock { Title = title, Rows = rows });
        }

        var secureBoot = Registry_ReadDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        report.Board = new List<InfoItem>
        {
            new("主板厂商", Str(board, "Manufacturer")),
            new("主板型号", Str(board, "Product")),
            new("主板版本", Str(board, "Version")),
            new("BIOS 厂商", Str(bios, "Manufacturer")),
            new("BIOS 版本", Str(bios, "SMBIOSBIOSVersion")),
            new("BIOS 日期", Date(bios, "ReleaseDate")),
            new("启动模式", NativeMethods.FirmwareMode()),
            new("TPM 模块", tpm is null ? "未检测到" : $"已启用 · {Str(tpm, "SpecVersion")}"),
            new("安全启动", secureBoot == 1 ? "已启用" : secureBoot == 0 ? "未启用" : "—"),
        };

        foreach (var disk in disks)
        {
            var model = Str(disk, "Model").Trim();
            var info = new DiskInfo
            {
                Model = model,
                Size = ByteFormatter.Format((ulong)Num(disk, "Size")),
                Type = Str(disk, "InterfaceType"),
                Serial = Str(disk, "SerialNumber").Trim(),
                Health = "—",
            };

            var physicalDisk = physical.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(Str(p, "FriendlyName")) &&
                (model.Contains(Str(p, "FriendlyName")[..Math.Min(12, Str(p, "FriendlyName").Length)])
                 || Str(p, "FriendlyName").Contains(model[..Math.Min(12, model.Length)])));

            if (physicalDisk is not null)
            {
                var media = Num(physicalDisk, "MediaType") switch
                {
                    3 => "机械硬盘",
                    4 => "固态硬盘",
                    5 => "存储级内存",
                    _ => Str(physicalDisk, "MediaType"),
                };
                info.Type = $"{Str(physicalDisk, "BusType")} / {media}";
                info.Health = Str(physicalDisk, "HealthStatus") switch
                {
                    "Healthy" => "健康",
                    "Warning" => "警告",
                    "Unhealthy" => "异常",
                    _ => Str(physicalDisk, "HealthStatus"),
                };
            }
            report.Disks.Add(info);
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var percent = drive.TotalSize == 0 ? 0 : (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100;
            report.Volumes.Add(new VolumeInfo
            {
                Mount = drive.Name,
                FileSystem = drive.DriveFormat,
                Total = ByteFormatter.Format((ulong)drive.TotalSize),
                Used = ByteFormatter.Format((ulong)(drive.TotalSize - drive.AvailableFreeSpace)),
                Free = ByteFormatter.Format((ulong)drive.AvailableFreeSpace),
                Percent = Math.Round(percent, 1),
                PercentText = $"{percent:F0}%",
            });
        }

        foreach (var nic in nets)
        {
            report.Networks.Add(new NetworkInfo
            {
                Name = Str(nic, "Description"),
                Ip = Join((string[]?)nic["IPAddress"]),
                Mac = Str(nic, "MACAddress"),
                Gateway = Join((string[]?)nic["DefaultIPGateway"]),
                Dns = Join((string[]?)nic["DNSServerSearchOrder"]),
                Assign = Str(nic, "DHCPEnabled") == "True" ? "自动 (DHCP)" : "手动",
            });
        }

        var monitorNames = monitors
            .Select(m => $"{MonString(m, "ManufacturerName")} {MonString(m, "UserFriendlyName")}".Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var battery = batteries.FirstOrDefault();
        report.Other = new List<InfoItem>
        {
            new("显示器", string.Join(" / ", monitorNames)),
            new("声卡", string.Join(" / ", sounds.Select(s => Str(s, "Name")))),
            new("电池", battery is null
                ? "未检测到电池（台式机）"
                : $"{Str(battery, "Name")} · 剩余 {Str(battery, "EstimatedChargeRemaining")}%"),
            new("电源状态", PowerStatus.PowerOnline ? "已接通电源" : "使用电池供电"),
        };

        return report;
    }

    // ------------------------------------------------------------------ WMI 辅助
    private static ManagementObject? First(string wql, string scope = "root\\cimv2")
        => All(wql, scope).FirstOrDefault();

    /// <summary>每条 WMI 查询的耗时（毫秒），--bench 时打印出来找瓶颈。</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> Timings = new();

    private static List<ManagementObject> All(string wql, string scope = "root\\cimv2")
    {
        var result = new List<ManagementObject>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(6);
            foreach (var item in searcher.Get())
                result.Add((ManagementObject)item);
        }
        catch
        {
            // 某些查询在精简系统 / 权限不足时会失败，忽略即可
        }
        watch.Stop();
        Timings[wql.Length > 44 ? wql[..44] + "…" : wql] = watch.ElapsedMilliseconds;
        return result;
    }

    private static string Str(ManagementObject? mo, string property)
    {
        if (mo is null) return "—";
        try
        {
            var value = mo[property];
            var text = value?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? "—" : text;
        }
        catch
        {
            return "—";
        }
    }

    private static long Num(ManagementObject? mo, string property)
    {
        if (mo is null) return 0;
        try
        {
            var value = mo[property];
            return value is null ? 0 : Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }

    private static string Date(ManagementObject? mo, string property)
    {
        if (mo is null) return "—";
        try
        {
            var raw = mo[property]?.ToString();
            if (string.IsNullOrWhiteSpace(raw)) return "—";
            return ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd");
        }
        catch
        {
            return "—";
        }
    }

    private static string MonString(ManagementObject mo, string property)
    {
        try
        {
            return mo[property] is ushort[] values
                ? new string(values.Where(v => v != 0).Select(v => (char)v).ToArray())
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Join(string[]? values)
        => values is null || values.Length == 0 ? "—" : string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)));

    private static int Registry_ReadDword(string path, string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            var value = key?.GetValue(name);
            return value is int number ? number : -1;
        }
        catch
        {
            return -1;
        }
    }
}
