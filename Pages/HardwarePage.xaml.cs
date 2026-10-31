using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Win11Toolbox.Controls;
using Win11Toolbox.Services;

namespace Win11Toolbox.Pages;

public partial class HardwarePage : Page
{
    private HardwareReport? _report;
    private bool _loading;
    private bool _rendered;

    public HardwarePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _loading = true;
        try
        {
            // 先用缓存（内存/磁盘）立刻画出来，再去后台刷新
            var cached = AppCache.Hardware;
            if (cached is not null)
                Show(cached);

            var fresh = await AppCache.RefreshAsync(TimeSpan.FromMinutes(10));
            if (fresh is not null)
                Show(fresh);
        }
        catch (Exception ex)
        {
            OverviewPanel.Children.Add(UiTheme.Secondary($"读取失败：{ex.Message}"));
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(HardwareReport report)
    {
        if (_rendered && ReferenceEquals(report, _report))
            return;

        _report = report;
        ClearPanels();
        Render(report);
        _rendered = true;
    }

    private void ClearPanels()
    {
        OverviewPanel.Children.Clear();
        CpuPanel.Children.Clear();
        MemoryPanel.Children.Clear();
        GpuPanel.Children.Clear();
        BoardPanel.Children.Clear();
        DiskPanel.Children.Clear();
        NetworkPanel.Children.Clear();
        OtherPanel.Children.Clear();
    }

    private void Render(HardwareReport report)
    {
        OverviewPanel.Children.Add(InfoCard("设备概览", report.Overview));
        OverviewPanel.Children.Add(InfoCard("关键部件", new List<InfoItem>
        {
            new("处理器", report.Overview[5].Value),
            new("内存", report.Overview[6].Value),
            new("显卡", report.Overview[7].Value),
            new("系统盘", report.Volumes.FirstOrDefault()?.Mount ?? "—"),
        }));

        CpuPanel.Children.Add(InfoCard("处理器信息", report.Cpu));

        MemoryPanel.Children.Add(InfoCard("内存概况", report.Memory));
        foreach (var module in report.Modules)
        {
            MemoryPanel.Children.Add(InfoCard($"{module.Slot} · {module.Size} · {module.Type}", new List<InfoItem>
            {
                new("当前频率", module.Speed),
                new("内存厂商", module.Vendor),
                new("型号", module.Part),
            }));
        }

        foreach (var gpu in report.Gpus)
            GpuPanel.Children.Add(InfoCard(gpu.Title, gpu.Rows));

        BoardPanel.Children.Add(InfoCard("主板与固件", report.Board));

        foreach (var disk in report.Disks)
        {
            DiskPanel.Children.Add(InfoCard(disk.Model, new List<InfoItem>
            {
                new("容量", disk.Size),
                new("接口 / 类型", disk.Type),
                new("健康状态", disk.Health),
                new("序列号", disk.Serial),
            }));
        }

        foreach (var volume in report.Volumes)
        {
            var card = new Card { Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
            var panel = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(UiTheme.BodyStrong($"本地磁盘 {volume.Mount}"));
            var caption = UiTheme.Caption($"{volume.FileSystem} · 已用 {volume.PercentText}");
            Grid.SetColumn(caption, 1);
            head.Children.Add(caption);
            panel.Children.Add(head);

            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = volume.Percent,
                Height = 4,
                Margin = new Thickness(0, 10, 0, 8),
            };
            panel.Children.Add(bar);

            var stats = new Grid();
            for (var i = 0; i < 3; i++)
                stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var values = new[] { ("总容量", volume.Total), ("已用", volume.Used), ("可用", volume.Free) };
            for (var i = 0; i < values.Length; i++)
            {
                var column = new StackPanel();
                column.Children.Add(UiTheme.Caption(values[i].Item1));
                column.Children.Add(UiTheme.Body(values[i].Item2));
                Grid.SetColumn(column, i);
                stats.Children.Add(column);
            }
            panel.Children.Add(stats);

            card.Content = panel;
            DiskPanel.Children.Add(card);
        }

        foreach (var nic in report.Networks)
        {
            NetworkPanel.Children.Add(InfoCard(nic.Name, new List<InfoItem>
            {
                new("IPv4 地址", nic.Ip),
                new("物理地址", nic.Mac),
                new("默认网关", nic.Gateway),
                new("DNS 服务器", nic.Dns),
                new("地址分配", nic.Assign),
            }, 90));
        }

        OtherPanel.Children.Add(InfoCard("外设与其他", report.Other));
    }

    private static Card InfoCard(string title, IEnumerable<InfoItem> rows, double keyWidth = 130)
    {
        var card = new Card { Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12) };
        var panel = new StackPanel();

        var header = UiTheme.BodyStrong(title);
        header.Margin = new Thickness(0, 0, 0, 2);
        panel.Children.Add(header);

        var index = 0;
        foreach (var row in rows)
            panel.Children.Add(new InfoRow(row.Key, row.Value, keyWidth, index++ > 0));

        card.Content = panel;
        return card;
    }
}
