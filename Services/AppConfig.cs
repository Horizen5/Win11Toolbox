using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Win11Toolbox.Services;

/// <summary>
/// 用户偏好：主题、窗口大小与位置、上次打开的页面。
/// 存在 %APPDATA%\Win11Toolbox\config.json（和旧 Python 版同一个文件，键名保持兼容）。
/// 读写都不抛异常 —— 配置坏了就当没有，绝不能因为它起不来。
/// </summary>
internal sealed class AppConfig
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win11Toolbox");

    private static readonly string FilePath = Path.Combine(Folder, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static AppConfig? _current;

    public static AppConfig Current => _current ??= Load();

    /// <summary>auto / light / dark。</summary>
    public string Theme { get; set; } = "auto";

    public double Width { get; set; } = 1240;
    public double Height { get; set; } = 820;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }

    /// <summary>上次停留的页面键（home / hardware / …）。</summary>
    public string LastPage { get; set; } = "home";

    // ------------------------------------------------------------------ 风扇曲线
    /// <summary>是否启用「按温度自动调速」。</summary>
    public bool FanCurve { get; set; }

    /// <summary>起转温度（℃）：低于它就用最低转速。</summary>
    public int FanCurveStart { get; set; } = 45;

    /// <summary>满载温度（℃）：高于它就跑满速。</summary>
    public int FanCurveFull { get; set; } = 80;

    /// <summary>最低转速（%）。</summary>
    public int FanCurveMin { get; set; } = 30;

    /// <summary>
    /// 风扇通道的自定义名。键是「芯片型号|通道名」，值是用户起的名字。
    /// 主板丝印上的 CPU_FAN / CHA_FAN 读不到，只能让用户自己标一次，之后记住。
    /// </summary>
    public Dictionary<string, string> FanNames { get; set; } = new();

    // ------------------------------------------------------------------ 页面键
    public static string KeyOf(Type pageType)
    {
        if (pageType == typeof(Pages.HomePage)) return "home";
        if (pageType == typeof(Pages.HardwarePage)) return "hardware";
        if (pageType == typeof(Pages.FanPage)) return "fan";
        if (pageType == typeof(Pages.ToolsPage)) return "tools";
        if (pageType == typeof(Pages.UtilitiesPage)) return "utilities";
        if (pageType == typeof(Pages.OptimizePage)) return "optimize";
        if (pageType == typeof(Pages.RightMenuPage)) return "rightmenu";
        if (pageType == typeof(Pages.TrayIconsPage)) return "tray";
        if (pageType == typeof(Pages.EasySettingsPage)) return "easysettings";
        if (pageType == typeof(Pages.SettingsPage)) return "settings";
        if (pageType == typeof(Pages.AboutPage)) return "about";
        return "home";
    }

    public static Type? TypeOf(string? key) => (key ?? "").ToLowerInvariant() switch
    {
        "home" => typeof(Pages.HomePage),
        "hardware" => typeof(Pages.HardwarePage),
        "fan" => typeof(Pages.FanPage),
        "tools" => typeof(Pages.ToolsPage),
        "utilities" => typeof(Pages.UtilitiesPage),
        "optimize" => typeof(Pages.OptimizePage),
        "rightmenu" => typeof(Pages.RightMenuPage),
        "tray" => typeof(Pages.TrayIconsPage),
        "easysettings" or "easy" => typeof(Pages.EasySettingsPage),
        "settings" => typeof(Pages.SettingsPage),
        "about" => typeof(Pages.AboutPage),
        _ => null,
    };

    // ------------------------------------------------------------------ 读写
    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg != null)
                {
                    cfg.Sanitize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏、被占用、没权限 —— 一律退回默认值
        }

        return new AppConfig();
    }

    /// <summary>把明显不合理的值拉回可用范围（手改坏文件、跨显示器换了分辨率都会遇到）。</summary>
    private void Sanitize()
    {
        if (Theme is not ("auto" or "light" or "dark")) Theme = "auto";

        if (double.IsNaN(Width) || Width < 900) Width = 1240;
        if (double.IsNaN(Height) || Height < 600) Height = 820;

        if (Left is double l && (double.IsNaN(l) || Math.Abs(l) > 20000)) Left = null;
        if (Top is double t && (double.IsNaN(t) || Math.Abs(t) > 20000)) Top = null;

        if (TypeOf(LastPage) == null) LastPage = "home";

        // 曲线参数：手改坏文件时别把滑块顶到范围外，也保证 起转 < 满载
        FanCurveStart = Math.Clamp(FanCurveStart, 30, 70);
        FanCurveFull = Math.Clamp(FanCurveFull, 55, 95);
        if (FanCurveFull <= FanCurveStart) FanCurveFull = Math.Min(95, FanCurveStart + 10);
        FanCurveMin = Math.Clamp(FanCurveMin, 0, 70);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var json = JsonSerializer.Serialize(this, Options);

            // 先写临时文件再替换，避免写一半断电留下半个 JSON
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            // 存不下就算了，不影响使用
        }
    }

    /// <summary>改一个值就立刻落盘（主题、上次页面这种点一下就要记住的）。</summary>
    public static void Remember(Action<AppConfig> mutate)
    {
        try
        {
            mutate(Current);
            Current.Save();
        }
        catch
        {
            // 忽略
        }
    }
}
