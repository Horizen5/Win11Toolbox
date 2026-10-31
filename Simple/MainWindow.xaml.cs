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

    public MainWindow()
    {
        InitializeComponent();
        SettingList.ItemsSource = _view;
        Loaded += OnLoaded;
    }

    // ------------------------------------------------------------------ 载入
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
            foreach (SettingItem item in EasySettingsData.Build())
            {
                bool on = SafeProbe(item);
                _allRows.Add(new SettingRow(item)
                {
                    IsOn = on,
                    StateText = on ? "已开启" : "默认状态",
                });
            }

            CategoryCombo.Items.Add(AllCategories);
            foreach (string c in _allRows.Select(r => r.Model.Category).Distinct())
                CategoryCombo.Items.Add(c);
            CategoryCombo.SelectedIndex = 0;

            RefreshList();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取设置失败：" + ex.Message;
        }

        ToolList.ItemsSource = ToolCatalog.Categories.SelectMany(c => c.Items).ToList();

        foreach (string[] link in EasySettingsData.QuickLinks())
            _links.Add(new LinkItem(link[0], link[1]));
        LinkList.ItemsSource = _links;
    }

    private static bool SafeProbe(SettingItem item)
    {
        try { return item.Probe(); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ 筛选
    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshList();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshList();

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

        var states = await Task.Run(() => _allRows.Select(r => SafeProbe(r.Model)).ToList());

        for (int i = 0; i < _allRows.Count && i < states.Count; i++)
        {
            _allRows[i].IsOn = states[i];
            _allRows[i].StateText = states[i] ? "已开启" : "默认状态";
        }

        UpdateCountHint();
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
