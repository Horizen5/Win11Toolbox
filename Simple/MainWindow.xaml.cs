using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Win11Toolbox.Services;
using Win11Toolbox.SettingsKit;

namespace Win11ToolboxSimple;

/// <summary>设置项在列表里的一行。</summary>
internal sealed class SettingRow : INotifyPropertyChanged
{
    public SettingRow(SettingItem model)
    {
        Model = model;
        StateText = "…";
    }

    public SettingItem Model { get; }

    public string Name => Model.Name;
    public string Desc => Model.Desc;

    private bool _isOn;
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            Raise(nameof(IsOn));
        }
    }

    private string _stateText;
    public string StateText
    {
        get => _stateText;
        set
        {
            _stateText = value;
            Raise(nameof(StateText));
            Raise(nameof(StateBrush));
        }
    }

    /// <summary>状态文字的颜色。只有「已开启」是绿的，失败是红的，其余灰。</summary>
    public Brush StateBrush => _stateText switch
    {
        "已开启" => Brushes.ForestGreen,
        "未生效" => Brushes.Firebrick,
        "写入失败" => Brushes.Firebrick,
        "待重启生效" => Brushes.DarkOrange,
        _ => Brushes.Gray,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>快捷入口的一行。</summary>
internal sealed record LinkItem(string Name, string Target);

public partial class MainWindow : Window
{
    private const string AllCategories = "（全部）";

    private readonly List<SettingRow> _allRows = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<SettingRow> _view = new();
    private readonly List<LinkItem> _links = new();

    /// <summary>写操作串行化 + 队列合并：连点两次不会把第二次丢掉。</summary>
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly List<(SettingRow Row, bool Want)> _pending = new();

    /// <summary>搜索框防抖：停手 180ms 才筛一次，见 <see cref="Search_Changed"/>。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180),
    };

    public MainWindow()
    {
        StartupTrace.Mark("MainWindow ctor 进入");
        InitializeComponent();
        StartupTrace.Mark("InitializeComponent 完成");

        // 标题栏带版本号：光看界面分不出新旧，只能去右键属性里翻。
        // 版本号从 AppInfo 取（唯一来源是 csproj 的 <Version>）。
        Title = "Win11 工具箱 · 简洁版 v" + AppInfo.Version;

        SettingList.ItemsSource = _view;
        Loaded += OnLoaded;

        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshList();
        };

        // --tab <0|1|2>：直接停在指定页签。
        // WPF 不接收合成鼠标消息（PostMessage WM_LBUTTONDOWN 试过，没用），
        // 想截另外两个页签的图只能从命令行指定，别指望脚本点得动。
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--tab");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int tab)
            && tab >= 0 && tab < Tabs.Items.Count)
            Tabs.SelectedIndex = tab;
    }

    // ------------------------------------------------------------------ 载入
    /// <summary>
    /// 两段式载入，和完整版「轻松设置」页是同一套办法。
    ///
    /// <para>
    /// 原来这里是「65 项全部 Probe 完 → 才第一次往列表里塞行」。Probe 要读注册表，
    /// 个别项还要跑 powercfg，整段实测 <b>2.4 秒</b>，全压在 UI 线程上：
    /// 窗口边框早就画出来了，内容区却一直是白的，看上去就是「打开后白屏好几秒」。
    /// </para>
    /// <para>
    /// 现在先把清单和行建出来（纯内存，毫秒级）立刻显示，状态列显示「…」表示未知；
    /// 再分批扔后台探测，每批回来就回填，用户能看到它一项项亮起来。
    /// </para>
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupTrace.Mark("Loaded 进入");

        if (Win11Toolbox.Admin.IsAdmin())
        {
            StatusText.Text = "管理员模式";
        }
        else
        {
            StatusText.Text = "普通权限：写 HKLM 的项会失败，点右下角按钮提权重开";
            ElevateButton.Visibility = Visibility.Visible;
        }

        try
        {
            // 第一段：建清单 + 建行。全部是内存操作，不碰系统。
            List<SettingItem> items;
            using (StartupTrace.Scope("EasySettingsData.Build"))
                items = EasySettingsData.Build();

            _allRows.Clear();
            foreach (SettingItem item in items)
                _allRows.Add(new SettingRow(item));       // StateText 初始为「…」

            using (StartupTrace.Scope("分类下拉 + RefreshList"))
            {
                CategoryCombo.Items.Add(AllCategories);
                foreach (string c in _allRows.Select(r => r.Model.Category).Distinct())
                    CategoryCombo.Items.Add(c);
                CategoryCombo.SelectedIndex = 0;
                RefreshList();
            }

            CountHint.Text = $"共 {_allRows.Count} 项，正在读取当前状态…";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取设置失败：" + ex.Message;
            return;
        }

        // 工具清单 / 快捷入口也是纯内存，顺手建掉（各 1ms）
        using (StartupTrace.Scope("工具清单 + 快捷入口"))
        {
            ToolList.ItemsSource = ToolCatalog.Categories.SelectMany(c => c.Items).ToList();

            foreach (string[] link in EasySettingsData.QuickLinks())
                _links.Add(new LinkItem(link[0], link[1]));
            LinkList.ItemsSource = _links;
        }

        StartupTrace.Mark("首屏内容已显示，开始后台探测");

        // 第二段：分批探测并回填
        await ProbeAllAsync();

        StartupTrace.Mark("Loaded 结束");
    }

    /// <summary>
    /// 分批探测所有设置项的当前状态。每批 4 项，批与批之间 <c>await</c> 让出 UI 线程，
    /// 保证列表随时能滚、能点。
    /// </summary>
    /// <param name="force">true 时连用户刚点过的行也一并覆盖（「刷新状态」按钮用）。</param>
    private async Task ProbeAllAsync(bool force = false)
    {
        const int BatchSize = 4;
        int total = _allRows.Count;

        for (int start = 0; start < total; start += BatchSize)
        {
            int from = start;
            int to = Math.Min(start + BatchSize, total);

            bool[] batch = await Task.Run(() =>
            {
                var result = new bool[to - from];
                for (int i = from; i < to; i++)
                    result[i - from] = SafeProbe(_allRows[i].Model);
                return result;
            });

            for (int i = 0; i < batch.Length; i++)
            {
                SettingRow row = _allRows[from + i];

                // 用户已经自己动过的行（StateText 不再是「…」）不覆盖，以他刚写进去的为准
                if (!force && row.StateText != "…") continue;

                row.IsOn = batch[i];
                row.StateText = batch[i] ? "已开启" : "默认状态";
            }

            CountHint.Text = $"共 {total} 项，已读取 {to} 项…";
        }

        UpdateCountHint();
        StatusText.Text = "状态已读取";
    }

    private static bool SafeProbe(SettingItem item)
    {
        try { return item.Probe(); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ 筛选
    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshList();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshList();

    /// <summary>
    /// 搜索框防抖。
    ///
    /// <para>
    /// 列表改成两列之后用的是 <c>WrapPanel</c>，它不虚拟化 —— 每次筛选都要把
    /// 65 个容器整个重建一遍，实测约 100 毫秒。不防抖的话每敲一个字卡一下，
    /// 打字像在敲木鱼。停手 180 毫秒再筛一次就够了。
    /// </para>
    /// </summary>
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RefreshList()
    {
        if (CategoryCombo.SelectedItem is not string category) return;

        string keyword = SearchBox.Text.Trim();

        // 原地增删，不重建 ItemsSource —— 重建会把滚动位置打回顶部，
        // 用户刚滚到中间点了某一项，结果列表跳回最上面，很难用
        _view.Clear();
        foreach (SettingRow row in _allRows)
        {
            if (category != AllCategories && row.Model.Category != category) continue;
            if (keyword.Length > 0
                && !row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                && !row.Desc.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            _view.Add(row);
        }

        UpdateCountHint();
    }

    private void UpdateCountHint()
    {
        CountHint.Text = $"显示 {_view.Count} / 共 {_allRows.Count} 项，已开启 {_allRows.Count(r => r.IsOn)} 项";
    }

    // ------------------------------------------------------------------ 应用
    private async void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not SettingRow row) return;
        bool want = box.IsChecked == true;

        // 同一项只保留最后一次点击想要的状态；已经在排队的不重复加
        int i = _pending.FindIndex(p => ReferenceEquals(p.Row, row));
        if (i >= 0) _pending[i] = (row, want);
        else _pending.Add((row, want));

        if (!await _applyLock.WaitAsync(0)) return;   // 已经有人在跑，它会把这个队列处理完
        try
        {
            while (_pending.Count > 0)
            {
                var (r, w) = _pending[0];
                _pending.RemoveAt(0);
                await ApplyOneAsync(r, w);
            }
        }
        finally { _applyLock.Release(); }
    }

    private async Task ApplyOneAsync(SettingRow row, bool want)
    {
        StatusText.Text = (want ? "正在应用 " : "正在恢复 ") + row.Name + "…";

        var (ok, error) = await Task.Run(() =>
        {
            try
            {
                row.Model.Set(want);
                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });

        // 写后回读，以注册表真实状态为准
        bool now = SafeProbe(row.Model);
        row.StateText = now ? "已开启" : "默认状态";
        row.IsOn = now;

        if (!ok || now != want)
        {
            StatusText.Text = row.Name + " " + (ok ? "未生效" : "写入失败")
                              + (error.Length > 0 ? "：" + error : "");
            if (!ok) row.StateText = "写入失败";
            else row.StateText = "未生效";
            return;
        }

        StatusText.Text = (want ? "已应用 " : "已恢复默认 ") + row.Name;
        UpdateCountHint();
    }

    // ------------------------------------------------------------------ 刷新
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在重新读取状态…";
        await ProbeAllAsync(force: true);
        StatusText.Text = "状态已刷新";
    }

    // ------------------------------------------------------------------ 工具 / 入口
    private void ToolList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ToolList.SelectedItem is not ToolItem item) return;
        try
        {
            string? message = ToolCatalog.Launch(item);
            StatusText.Text = message ?? ("已启动 " + item.Name);
        }
        catch (Exception ex)
        {
            StatusText.Text = item.Name + " 启动失败：" + ex.Message;
        }
    }

    private void LinkList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LinkList.SelectedItem is not LinkItem link) return;
        EasySettingsData.OpenLink(link.Target);
        StatusText.Text = EasySettingsData.LastError.Length > 0
            ? EasySettingsData.LastError
            : "已打开 " + link.Name;
    }

    // ------------------------------------------------------------------ 提权
    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0)
            {
                StatusText.Text = "找不到自身路径，请手动右键「以管理员身份运行」";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText.Text = "提权失败：" + ex.Message;
        }
    }
}
