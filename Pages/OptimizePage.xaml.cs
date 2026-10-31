using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;
using Win11Toolbox.Controls;
using Win11Toolbox.Services;

namespace Win11Toolbox.Pages;

public partial class OptimizePage : Page
{
    private static DateTime _lastScan = DateTime.MinValue;
    private static string _tempText = "尚未扫描";
    private static string _recycleText = "";
    private long _tempSize;
    private List<(string Guid, string Name, bool Active)> _plans = new();

    public OptimizePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 20 秒内重复进入页面不重新扫描（扫临时目录本身就要 1 秒左右），但要复原上次的文案
        if (DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(20))
        {
            await ScanAsync();
        }
        else
        {
            TempInfo.Text = _tempText;
            RecycleInfo.Text = _recycleText;
        }

        RefreshStartup();
        LoadPowerPlans();
    }

    // ------------------------------------------------------------------ 清理
    private async Task ScanAsync()
    {
        TempInfo.Text = "正在扫描临时文件…";
        try
        {
            var (size, count) = await Task.Run(() => ShellTools.TempSize(false));
            _tempSize = size;
            _lastScan = DateTime.UtcNow;
            _tempText = $"临时文件：{ByteFormatter.Format((ulong)size)}（{count} 个文件）";
            TempInfo.Text = _tempText;

            var (recycleSize, recycleCount) = await Task.Run(ShellTools.RecycleBin);
            _recycleText = $"回收站：{ByteFormatter.Format((ulong)recycleSize)}（{recycleCount} 个文件）";
            RecycleInfo.Text = _recycleText;
        }
        catch (Exception ex)
        {
            TempInfo.Text = $"扫描失败：{ex.Message}";
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("清理临时文件",
                $"即将删除临时目录中的文件，预计释放 {ByteFormatter.Format((ulong)_tempSize)}。\n正在被占用的文件会被跳过，是否继续？"))
            return;

        var (freed, deleted, skipped) = await Task.Run(() => ShellTools.CleanTemp(false));
        TempInfo.Text = $"已删除 {deleted} 个文件，释放 {ByteFormatter.Format((ulong)freed)}（跳过 {skipped} 个占用中的文件）";
        await ScanAsync();
    }

    private async void EmptyRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("清空回收站", "回收站中的文件将被永久删除，是否继续？"))
            return;

        var ok = await Task.Run(ShellTools.EmptyRecycleBin);
        RecycleInfo.Text = ok ? "回收站已清空" : "清空失败，可能有文件正在被使用";
        await ScanAsync();
    }

    // ------------------------------------------------------------------ 维护
    private void FlushDns_Click(object sender, RoutedEventArgs e)
        => MaintainInfo.Text = ShellTools.FlushDns() ? "DNS 解析缓存已刷新" : "刷新失败";

    private void IconCache_Click(object sender, RoutedEventArgs e)
    {
        ShellTools.RefreshIconCache();
        MaintainInfo.Text = "已重建图标缓存，若图标未刷新可重启资源管理器";
    }

    private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("重启资源管理器", "桌面和任务栏会短暂闪烁，是否继续？"))
            return;

        ShellTools.RestartExplorer();
        MaintainInfo.Text = "已重启资源管理器";
    }

    private async void Sfc_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("系统文件检查", "将以管理员身份运行 sfc /scannow，扫描可能需要 10 分钟以上，是否继续？"))
            return;

        ShellTools.RunElevated("sfc /scannow");
        MaintainInfo.Text = "已在新的管理员窗口中开始扫描";
    }

    private void CleanMgr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start("cleanmgr.exe");
        }
        catch
        {
            // 忽略
        }
    }

    // ------------------------------------------------------------------ 启动项
    private void RefreshStartup_Click(object sender, RoutedEventArgs e) => RefreshStartup();

    private void RefreshStartup()
    {
        StartupList.Children.Clear();
        var items = ShellTools.StartupItems();
        StartupCount.Text = $"启动项（{items.Count}）";

        foreach (var item in items.Take(12))
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            row.Children.Add(NameCell(item));

            var scope = UiTheme.Caption($"{item.Scope} · {item.Source}");
            Grid.SetColumn(scope, 1);
            row.Children.Add(scope);

            var command = UiTheme.Caption(item.Command);
            command.TextTrimming = TextTrimming.CharacterEllipsis;
            command.ToolTip = item.Command;
            Grid.SetColumn(command, 2);
            row.Children.Add(command);

            StartupList.Children.Add(row);
        }

        if (items.Count > 12)
            StartupList.Children.Add(UiTheme.Caption($"还有 {items.Count - 12} 项未显示，请在任务管理器中查看"));
    }

    /// <summary>名称前带图标：图标从启动项对应的 exe / lnk 里取，和任务管理器「启动」页一致。</summary>
    private static FrameworkElement NameCell(StartupEntry item)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var path = ShellTools.ExecutablePath(item);
        var icon = string.IsNullOrEmpty(path) ? null : ShellIcon.FromPath(path, small: true);
        if (icon is not null)
        {
            var image = new System.Windows.Controls.Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 16px 小图标用双线性缩放，比 Fant 滤镜更锐利（和列表行图标一个道理）
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.LowQuality);
            panel.Children.Add(image);
        }

        var name = UiTheme.Body(item.Name);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        panel.Children.Add(name);
        return panel;
    }

    private void OpenTaskManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    // ------------------------------------------------------------------ 电源
    private void LoadPowerPlans()
    {
        _plans = ShellTools.PowerPlans();
        PowerCombo.Items.Clear();
        foreach (var plan in _plans)
            PowerCombo.Items.Add(plan.Name);

        var activeIndex = _plans.FindIndex(p => p.Active);
        if (activeIndex >= 0)
            PowerCombo.SelectedIndex = activeIndex;
    }

    private void ApplyPower_Click(object sender, RoutedEventArgs e)
    {
        var index = PowerCombo.SelectedIndex;
        if (index < 0 || index >= _plans.Count)
            return;

        var ok = ShellTools.SetPowerPlan(_plans[index].Guid);
        MaintainInfo.Text = ok ? $"已切换到「{_plans[index].Name}」" : "切换失败，可能需要管理员权限";
    }

    // ------------------------------------------------------------------ 确认框
    /// <summary>用独立的 MessageBox 窗口，避免依赖 ContentDialog 的宿主导致点了没反应。</summary>
    private static async Task<bool> ConfirmAsync(string title, string message)
    {
        try
        {
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                ShowTitle = true,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
            };
            return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        catch
        {
            return false;
        }
    }
}
