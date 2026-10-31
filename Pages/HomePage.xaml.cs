using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;
using Win11Toolbox.Controls;
using Win11Toolbox.Services;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace Win11Toolbox.Pages;

public partial class HomePage : Page
{
    private readonly SystemMonitor _monitor = new();
    private readonly MetricCard _cpuCard;
    private readonly MetricCard _memCard;
    private readonly MetricCard _diskCard;
    private readonly MetricCard _netCard;
    private HardwareReport? _report;

    /// <summary>自检用：收到过多少次监控快照。</summary>
    public static int SnapshotCount { get; private set; }

    public HomePage()
    {
        InitializeComponent();

        // CPU 用自适应量程（负载忽高忽低，固定 0–100 时低负载就是一条贴底的线）；
        // 内存 / 磁盘是"绝对占比"，必须固定 0–100，否则 43% 会被放大成满格；
        // 网络是速率，绝对数值本身没意义，用自适应。
        _cpuCard = new MetricCard(SymbolRegular.Gauge24, "CPU 使用率", "%", floor: 30);
        _memCard = new MetricCard(SymbolRegular.DataUsage24, "内存占用", "%", ceiling: 100);
        _diskCard = new MetricCard(SymbolRegular.Storage24, "系统盘占用", "%", ceiling: 100);
        _netCard = new MetricCard(SymbolRegular.Globe24, "网络下载", "MB/s", percent: false, floor: 0.1);

        // 四张卡之间留 12px 空隙（Grid 里第 1/3/5 列是空列），不然会连成一整条
        var cards = new[] { _cpuCard, _memCard, _diskCard, _netCard };
        for (int i = 0; i < cards.Length; i++)
        {
            Grid.SetColumn(cards[i], i * 2);
            MetricsGrid.Children.Add(cards[i]);
        }

        BuildQuickEntries();
        LoadHeroShot();

        _monitor.Snapshot += OnSnapshot;

        // 外层 NavigationView 的 ScrollViewer 用无限高度量页面，页面里这层 ScrollViewer
        // 就会跟着撑到内容高、自己永远滚不动（滚轮还会被它吃掉）。锁住外层，
        // 让页面自己这层拿到可视区高度，滚轮和右侧滚动条才会落到它身上。
        Loaded += (_, _) => ViewportLock.Lock(this);
        // 切深浅色后重画一遍：状态色是算出来的，不重来就还是旧主题的颜色
        UiTheme.ThemeChanged += () => UiTheme.Time("Home", RenderChecks);
        Unloaded += (_, _) => ViewportLock.Unlock(this);

        Loaded += OnLoaded;
        Unloaded += (_, _) => _monitor.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 每次进入页面都重新启动计时器：离开时会 Stop，回到主页不启动就会"冻住"
        _monitor.Start();

        if (_report is null)
            await LoadInfoAsync();
    }

    private void OnSnapshot(MonitorSnapshot snapshot)
    {
        SnapshotCount++;
        _cpuCard.Update($"{snapshot.Cpu:F0}", $"{snapshot.ProcessCount} 个进程 · 每秒刷新", snapshot.Cpu);
        _memCard.Update($"{snapshot.MemoryPercent:F0}",
            $"{ByteFormatter.Format(snapshot.MemoryUsed)} / {ByteFormatter.Format(snapshot.MemoryTotal)}",
            snapshot.MemoryPercent);
        _diskCard.Update($"{snapshot.DiskPercent:F0}",
            $"剩余 {ByteFormatter.Format(snapshot.DiskFree)}", snapshot.DiskPercent);

        var down = snapshot.NetDown / 1024 / 1024;
        var up = snapshot.NetUp / 1024 / 1024;
        _netCard.Update($"{down:F2}", $"上传 {up:F2} MB/s", down);
    }

    // ------------------------------------------------------------------ 概览
    private async Task LoadInfoAsync()
    {
        // 缓存优先：有缓存就立刻显示，再去后台刷新
        var cached = AppCache.Hardware;
        if (cached is not null)
            ApplyReport(cached);
        else
            HeroName.Text = "正在读取设备信息…";

        var fresh = await AppCache.RefreshAsync(TimeSpan.FromMinutes(10));
        if (fresh is not null)
            ApplyReport(fresh);
    }

    private void ApplyReport(HardwareReport report)
    {
        _report = report;
        HeroName.Text = report.Overview[0].Value;
        HeroModel.Text = report.Overview[1].Value;          // 设备型号，系统设置里就是这行
        HeroOs.Text = $"{report.Overview[2].Value} · Build {ExtractBuild(report.Overview[3].Value)}";
        HeroCpu.Text = report.Overview[5].Value;
        HeroMem.Text = MemorySummary(report);
        HeroGpu.Text = report.Overview[7].Value;
        RunChecks();
    }

    /// <summary>设备缩略图用系统自带的那张 bloom 壁纸，和设置页里的设备图一致；取不到就留渐变底。</summary>
    private void LoadHeroShot()
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Web", "Wallpaper", "Windows", "img0.jpg");
            if (!System.IO.File.Exists(path))
                return;

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 360;      // 缩略图很小，不必整张解码
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            HeroShot.Source = bitmap;
        }
        catch
        {
            // 取不到就用 XAML 里的渐变底
        }
    }

    /// <summary>重命名：和系统设置一样只写入待生效的名字，重启后生效。</summary>
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        string current = HeroName.Text;
        string? name = await InputDialog.ShowAsync(
            Window.GetWindow(this),
            "重命名这台电脑",
            "新的计算机名",
            current,
            "最多 15 个字符，只能用英文字母、数字和连字符。重启后才会生效。",
            "下一步");

        if (name is null) return;
        if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
            return;

        var result = ComputerRename.Apply(name);
        await ShowInfoAsync(result.Ok ? "已提交改名" : "没有改成", result.Message);
    }

    private static async Task ShowInfoAsync(string title, string message)
    {
        try
        {
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                CloseButtonText = "知道了",
            };
            await dialog.ShowDialogAsync();
        }
        catch { }
    }

    private static string ExtractBuild(string version)
    {
        var start = version.IndexOf("Build ", StringComparison.Ordinal);
        return start >= 0 ? version[(start + 6)..].TrimEnd(')') : version;
    }

    private static string MemorySummary(HardwareReport report)
    {
        if (report.Modules.Count == 0)
            return report.Overview[6].Value;

        // 用总容量而不是第一条的容量：双通道 2×16 GB 时，
        // 系统设置里「已安装的 RAM」显示的也是合计值。
        string total = report.Overview[6].Value;
        string type = report.Modules
            .Select(m => m.Type)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t) && t != "—") ?? "—";
        string count = $"共 {report.Modules.Count} 条";
        return type == "—" ? $"{total} · {count}" : $"{total} · {type} · {count}";
    }

    // ------------------------------------------------------------------ 体检

    /// <summary>上一次体检的结果。切主题时只拿它重画，不重新算 —— 见 <see cref="RenderChecks"/>。</summary>
    private List<(string Level, string Title, string Detail)> _checks = new();

    /// <summary>
    /// 重新体检一遍（读内存、磁盘、启动项、回收站 —— 这几项都不便宜）。
    /// 只在进页面、以及用户点"重新检测"时调用。
    /// </summary>
    private void RunChecks()
    {
        _checks = CollectChecks();
        RenderChecks();
    }

    /// <summary>
    /// 只按 <see cref="_checks"/> 重建那几行，不做任何系统调用。
    ///
    /// 切主题必须走这里：之前订阅的是 <see cref="RunChecks"/>，结果换一次颜色要去遍历回收站，
    /// 实测卡了 4.3 秒（<c>--themeflash</c> 里 Home=4278ms 就是它）。
    /// 换颜色是纯视觉的事，重新读系统状态是两码事。
    /// </summary>
    private void RenderChecks()
    {
        CheckList.Children.Clear();
        foreach (var (level, title, detail) in _checks)
            CheckList.Children.Add(BuildCheckRow(level, title, detail));

        var ok = _checks.Count(r => r.Level == "ok");
        CheckSummary.Text = $"{ok}/{_checks.Count} 项正常";
    }

    /// <summary>真正干活的那一段：把所有检查项跑一遍，返回结果表。</summary>
    private List<(string Level, string Title, string Detail)> CollectChecks()
    {
        var results = new List<(string Level, string Title, string Detail)>();

        var (_, total, _) = NativeMethods.Memory();
        if (total >= 8UL * 1024 * 1024 * 1024)
            results.Add(("ok", "内存容量充足", $"共 {ByteFormatter.Format(total, 0)}"));
        else if (total >= 4UL * 1024 * 1024 * 1024)
            results.Add(("warn", "内存容量偏小", $"共 {ByteFormatter.Format(total, 0)}，建议升级到 8 GB 以上"));
        else
            results.Add(("error", "内存容量不足", $"仅 {ByteFormatter.Format(total, 0)}，日常使用可能卡顿"));

        try
        {
            var drive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!);
            var freePercent = (double)drive.AvailableFreeSpace / drive.TotalSize * 100;
            if (freePercent >= 20)
                results.Add(("ok", "系统盘空间充足", $"剩余 {ByteFormatter.Format((ulong)drive.AvailableFreeSpace)}（{freePercent:F0}%）"));
            else if (freePercent >= 10)
                results.Add(("warn", "系统盘空间偏少", $"仅剩 {ByteFormatter.Format((ulong)drive.AvailableFreeSpace)}（{freePercent:F0}%）"));
            else
                results.Add(("error", "系统盘空间告急", $"仅剩 {ByteFormatter.Format((ulong)drive.AvailableFreeSpace)}，建议立即清理"));
        }
        catch
        {
            // 忽略
        }

        if (_report is not null)
        {
            var unhealthy = _report.Disks
                .Where(d => d.Health.Contains("警告") || d.Health.Contains("异常")
                            || d.Health.Contains("Warning") || d.Health.Contains("Unhealthy"))
                .ToList();
            if (unhealthy.Count > 0)
                results.Add(("warn", "磁盘健康异常", string.Join("、", unhealthy.Select(d => d.Model))));
            else if (_report.Disks.Count > 0)
                results.Add(("ok", "磁盘健康正常", $"共检测到 {_report.Disks.Count} 块磁盘"));
        }

        try
        {
            var startup = ShellTools.StartupItems();
            results.Add(startup.Count <= 12
                ? ("ok", "启动项数量正常", $"共 {startup.Count} 项开机自启")
                : ("warn", "开机启动项较多", $"共 {startup.Count} 项，可在任务管理器中精简"));
        }
        catch
        {
            // 忽略
        }

        try
        {
            var (size, count) = ShellTools.RecycleBin();
            results.Add(size > 2L * 1024 * 1024 * 1024
                ? ("warn", "回收站占用较大", $"{count} 个文件，共 {ByteFormatter.Format((ulong)size)}")
                : ("ok", "回收站占用正常", $"{count} 个文件，共 {ByteFormatter.Format((ulong)size)}"));
        }
        catch
        {
            // 忽略
        }

        return results;
    }

    private static UIElement BuildCheckRow(string level, string title, string detail)
    {
        // 语义色跟着主题走：写死的深绿/深红在深色底上根本读不出来
        var color = level switch
        {
            "ok" => ((SolidColorBrush)UiTheme.Success).Color,
            "warn" => ((SolidColorBrush)UiTheme.Caution).Color,
            "error" => ((SolidColorBrush)UiTheme.Critical).Color,
            _ => ((SolidColorBrush)UiTheme.Neutral).Color,
        };

        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0),
        };
        grid.Children.Add(dot);

        var texts = new StackPanel();
        texts.Children.Add(UiTheme.BodyStrong(title));
        var detailBlock = UiTheme.Caption(detail);
        detailBlock.TextWrapping = TextWrapping.Wrap;
        detailBlock.Margin = new Thickness(0, 1, 0, 0);
        texts.Children.Add(detailBlock);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        return grid;
    }

    // ------------------------------------------------------------------ 快捷入口
    private void BuildQuickEntries()
    {
        var picks = new[]
        {
            "任务管理器", "设备管理器", "磁盘管理", "系统信息",
            "网络连接", "服务和应用程序", "注册表编辑器", "磁盘清理",
        };

        var all = ToolCatalog.Categories.SelectMany(c => c.Items).ToList();
        foreach (var name in picks)
        {
            var item = all.FirstOrDefault(i => i.Name == name);
            if (item is null) continue;

            var tile = new ToolTile(item.Name, item.Description, ToolCatalog.Icon(item), ToolCatalog.Glyph(item))
            {
                Margin = new Thickness(2),
            };
            tile.Clicked += () => Launch(item);
            QuickGrid.Children.Add(tile);
        }
    }

    private void Launch(ToolItem item)
    {
        try
        {
            // 非 null 表示「没能按原样启动，但有话要说」（如 Windows 终端没装 → 已退回命令提示符）
            var notice = ToolCatalog.Launch(item);
            if (notice is not null)
                ShowMessage("提示", notice);
        }
        catch (Exception ex)
        {
            ShowMessage("启动失败", ex.Message);
        }
    }

    private static void ShowMessage(string title, string message)
    {
        try
        {
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                CloseButtonText = "好的",
                ShowTitle = true,
            };
            _ = dialog.ShowDialogAsync();
        }
        catch
        {
            // 忽略弹窗失败
        }
    }

    // ------------------------------------------------------------------ 事件
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await AppCache.RefreshAsync(TimeSpan.Zero, force: true);
            var report = AppCache.Hardware;
            if (report is not null)
                ApplyReport(report);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => RunChecks();
}
