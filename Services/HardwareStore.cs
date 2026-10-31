using System;
using System.IO;
using System.Text.Json;

namespace Win11Toolbox.Services;

/// <summary>
/// 硬件信息的磁盘缓存：WMI 采集要 2 秒左右，缓存到 JSON 后再次打开页面可以立刻显示，
/// 然后在后台刷新，刷新完再更新界面。
/// </summary>
public static class HardwareStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        // 内存条 / 硬盘 / 分区这些模型用的是公有字段而不是属性，
        // 默认的序列化器只认属性，不开这个开关写出来就是一堆空对象 {}。
        IncludeFields = true,
    };

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win11Toolbox");

    private static string FilePath => Path.Combine(Folder, "hardware.json");

    public static HardwareReport? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var report = JsonSerializer.Deserialize<HardwareReport>(json, Options);
            if (report is null || report.Overview.Count == 0)
                return null;

            // 超过 7 天的缓存当没有，避免显示去年的配置
            if (DateTime.Now - report.CollectedAt > TimeSpan.FromDays(7))
                return null;

            return report;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(HardwareReport report)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(report, Options));
        }
        catch
        {
            // 写缓存失败不影响使用
        }
    }
}
