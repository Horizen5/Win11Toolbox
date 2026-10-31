using System;
using Win11Toolbox.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Win11Toolbox.Tray;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Win11Toolbox.Pages;

/// <summary>托盘列表的一行。</summary>
internal sealed class TrayRow : INotifyPropertyChanged
{
    public TrayRow(TrayIcon model) => Model = model;

    public TrayIcon Model { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _checked;
    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Raise(nameof(Checked)); }
    }

    public ImageSource? Icon => Model.Icon;
    public string AppName => string.IsNullOrEmpty(Model.AppName) ? "（未命名）" : Model.AppName;
    public string ExePath => Model.ExePath.Length > 0 ? Model.ExePath : "（未定位到程序）";

    public string SettingsState => Model.SettingsState switch
    {
        1 => "始终显示",
        0 => "在溢出区",
        _ => Model.SettingsKey.Length == 0 ? "清单未收录" : "未知",
    };

    private static Brush BrushOn => UiTheme.Success;
    private static Brush BrushOff => UiTheme.Neutral;
    private static Brush BrushUnknown => UiTheme.Caution;

    public Brush StateBrush => Model.SettingsState == 1
        ? BrushOn
        : Model.SettingsState == 0
            ? BrushOff
            : BrushUnknown;

    public string Where
    {
        get
        {
            if (Model.SettingsKey.Length == 0) return "清单里没有";
            if (!Model.Live) return "没在运行";
            return Model.SettingsState == 1 ? "运行中 · 任务栏" : "运行中 · 溢出区";
        }
    }

    public string Tip
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine("应用：" + AppName);
            if (Model.Tooltip.Length > 0) sb.AppendLine("悬停原文：" + Model.Tooltip);
            if (Model.ExePath.Length > 0) sb.AppendLine("进程：" + Model.ExePath);
            sb.AppendLine("当前设置：" + SettingsState);
            if (Model.SettingsName.Length > 0) sb.AppendLine("清单条目：" + Model.SettingsName);
            sb.Append("右键这一行也能直接切换。");
            return sb.ToString();
        }
    }
}

public partial class TrayIconsPage : Page, IRefreshablePage
{
    private readonly List<TrayIcon> _all = new();
    private readonly ObservableCollection<TrayRow> _rows = new();
    private readonly List<string> _log = new();
    private readonly Dictionary<string, bool> _changed = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;

    public TrayIconsPage()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        Loaded += OnLoaded;

        // 切深浅色后把列表重绑一遍：行的状态色是算出来的属性，
        // 不重绑就会一直停在旧主题的颜色上（深色底上的深绿看不见）。
        UiTheme.ThemeChanged += () => UiTheme.Time("TrayIcons", RenderList);

        // 本页是满屏布局（内部列表自己滚），把外层 NavigationView 的滚动关掉，
        // 否则外层会用无限高测量本页，列表虚拟化失效、标题被切掉。
        Loaded += (_, _) => ViewportLock.Lock(this);
        Unloaded += (_, _) => ViewportLock.Unlock(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 读注册表是毫秒级的，进页面直接列出来，不用先点按钮
        if (_all.Count == 0) await RefreshAsync();
    }

    private void Log(string message)
    {
        UiThread.RunSafe(StatusLine, () =>
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_log.Count > 300) _log.RemoveRange(0, _log.Count - 300);
            StatusLine.Text = message;
        });
    }

    private async void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        var text = _log.Count == 0 ? "（还没有操作记录）" : string.Join(Environment.NewLine, _log);
        await ShowAsync("操作日志", text);
    }

    /// <summary>UI Automation 在 STA 线程上最稳，这里单独开一个。</summary>
    private static Task<T> RunSta<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // ------------------------------------------------------------------ 读取
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ------------------------------------------------------------------ F5 / Ctrl+F
    public void RefreshPage()
    {
        if (_busy) return;
        _ = RefreshAsync();
    }

    public void FocusSearch()
    {
        // 这一页没有搜索框，F5 重读就够了
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        StatusLine.Text = "正在读取托盘图标清单…";
        Log("开始读取：直接读注册表 NotifyIconSettings，不打开任何窗口。");

        try
        {
            var (list, lines) = await RunSta(() =>
            {
                var lb = new StringBuilder();
                var items = TrayCore.Load(lb);
                var logs = lb.ToString().Split('\n')
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                return (items, logs);
            });

            _all.Clear();
            _all.AddRange(list);
            foreach (var line in lines) Log(line);

            int live = _all.Count(t => t.Live);
            int inList = _all.Count(t => t.SettingsKey.Length > 0);
            Log($"读取完成：共 {_all.Count} 项（清单 {inList} 项），正在托盘里 {live} 个。");
            if (inList == 0)
                Log("⚠ 没读到托盘图标清单，现在只能查看、无法切换。");
            RenderList();
        }
        catch (Exception ex)
        {
            Log("读取失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void RenderList()
    {
        bool runningOnly = RunningOnly.IsChecked == true;
        _rows.Clear();
        foreach (var ti in _all)
        {
            if (runningOnly && !ti.Live) continue;
            _rows.Add(new TrayRow(ti));
        }
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => RenderList();

    // ------------------------------------------------------------------ 切换
    private async void ShowChecked_Click(object sender, RoutedEventArgs e) => await ApplyAsync(true);
    private async void HideChecked_Click(object sender, RoutedEventArgs e) => await ApplyAsync(false);

    private async void RowShow_Click(object sender, RoutedEventArgs e) => await ApplyOneFromMenu(sender, true);
    private async void RowHide_Click(object sender, RoutedEventArgs e) => await ApplyOneFromMenu(sender, false);

    private async Task ApplyOneFromMenu(object sender, bool wantOn)
    {
        var row = MenuRowOf(sender);
        if (row is null) return;
        await ApplyAsync(wantOn, new List<TrayIcon> { row.Model });
    }

    private static TrayRow? MenuRowOf(object sender)
    {
        if (sender is not MenuItem item) return null;
        if (item.Parent is not ContextMenu menu) return null;
        return (menu.PlacementTarget as FrameworkElement)?.DataContext as TrayRow;
    }

    private void RowOpenDir_Click(object sender, RoutedEventArgs e)
    {
        var row = MenuRowOf(sender);
        if (row is null || row.Model.ExePath.Length == 0) return;
        try
        {
            string dir = System.IO.Path.GetDirectoryName(row.Model.ExePath) ?? "";
            if (dir.Length > 0) RightMenu.RightMenuActions.OpenDirectory(dir);
        }
        catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
    }

    private async Task ApplyAsync(bool wantOn, List<TrayIcon>? explicitTargets = null)
    {
        if (_busy) return;

        var targets = explicitTargets ?? _rows.Where(r => r.Checked).Select(r => r.Model).ToList();
        if (targets.Count == 0)
        {
            await ShowAsync("没有勾选项目", "请先勾选要操作的系统托盘图标。");
            return;
        }

        var items = new List<KeyValuePair<string, bool>>();
        foreach (var ti in targets)
        {
            if (ti.SettingsKey.Length == 0)
            {
                Log("跳过 " + ti.AppName + "：清单里没有这一项，无法自动切换。");
                continue;
            }
            items.Add(new KeyValuePair<string, bool>(ti.SettingsKey, wantOn));
            if (!_changed.ContainsKey(ti.SettingsKey))
                _changed[ti.SettingsKey] = ti.SettingsState == 1;
        }
        if (items.Count == 0)
        {
            Log("没有可切换的项目。");
            return;
        }

        _busy = true;
        StatusLine.Text = (wantOn ? "正在固定到任务栏…" : "正在收进溢出区…");
        Log((wantOn ? "固定到任务栏：" : "收进溢出区：") + items.Count + " 项…");

        try
        {
            var (result, lines) = await RunSta(() =>
            {
                var lb = new StringBuilder();
                string r = TraySettings.Apply(items, lb);
                var logs = lb.ToString().Split('\n')
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                return (r, logs);
            });
            foreach (var line in lines) Log(line);
            Log(result);
        }
        catch (Exception ex)
        {
            Log("切换失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }

        await RefreshSilentlyAsync();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_changed.Count == 0)
        {
            await ShowAsync("没有可撤销的改动", "本次会话还没有改过任何托盘图标。");
            return;
        }

        var items = _changed.Select(kv => new KeyValuePair<string, bool>(kv.Key, kv.Value)).ToList();
        _busy = true;
        Log("恢复本次修改：" + items.Count + " 项…");
        try
        {
            var (result, lines) = await RunSta(() =>
            {
                var lb = new StringBuilder();
                string r = TraySettings.Apply(items, lb);
                var logs = lb.ToString().Split('\n')
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                return (r, logs);
            });
            foreach (var line in lines) Log(line);
            Log(result);
            _changed.Clear();
        }
        catch (Exception ex) { Log("恢复失败：" + ex.Message); }
        finally { _busy = false; }

        await RefreshSilentlyAsync();
    }

    /// <summary>切换完刷新一次实况，但不再弹提示（开关状态已经变了）。</summary>
    private async Task RefreshSilentlyAsync()
    {
        try
        {
            var list = await RunSta(() => TrayCore.Load(new StringBuilder()));
            _all.Clear();
            _all.AddRange(list);
            RenderList();
        }
        catch (Exception ex)
        {
            Log("刷新实况失败（设置本身已生效）：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 对话框
    private static async Task ShowAsync(string title, string message)
    {
        try
        {
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                ShowTitle = true,
                CloseButtonText = "关闭",
            };
            await dialog.ShowDialogAsync();
        }
        catch { }
    }
}
