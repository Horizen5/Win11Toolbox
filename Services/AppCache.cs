using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Win11Toolbox.Services;

/// <summary>
/// 全局缓存：硬件信息先给磁盘/内存里的旧数据（毫秒级），再后台刷新；
/// 工具图标要调 SHGetFileInfo，也缓存起来。
/// </summary>
public static class AppCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Icons = new();

    private static HardwareReport? _hardware;
    private static bool _refreshing;

    /// <summary>硬件信息刷新完成后触发，界面可以据此重绘。</summary>
    public static event Action<HardwareReport>? HardwareUpdated;

    /// <summary>立刻可用的数据（内存缓存 → 磁盘缓存 → null）。</summary>
    public static HardwareReport? Hardware
    {
        get
        {
            lock (Gate)
            {
                _hardware ??= HardwareStore.Load();
                return _hardware;
            }
        }
    }

    /// <summary>数据超过 maxAge 时后台重新采集；force 表示强制刷新。</summary>
    public static async Task<HardwareReport?> RefreshAsync(TimeSpan maxAge, bool force = false)
    {
        var current = Hardware;
        if (!force && current is not null && DateTime.Now - current.CollectedAt <= maxAge)
            return current;

        lock (Gate)
        {
            if (_refreshing)
                return current;
            _refreshing = true;
        }

        try
        {
            var fresh = await Task.Run(SystemInfo.Build);
            lock (Gate)
            {
                _hardware = fresh;
            }

            HardwareStore.Save(fresh);
            HardwareUpdated?.Invoke(fresh);
            return fresh;
        }
        catch
        {
            return current;
        }
        finally
        {
            lock (Gate)
            {
                _refreshing = false;
            }
        }
    }

    public static ImageSource? Icon(ToolItem item)
    {
        lock (Gate)
        {
            if (Icons.TryGetValue(item.Target, out var cached))
                return cached;

            var icon = ToolCatalog.Icon(item);
            Icons[item.Target] = icon;
            return icon;
        }
    }
}
