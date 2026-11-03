using System;
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
using Win11Toolbox.Controls;
using Win11Toolbox.Services;

namespace Win11Toolbox.Pages;

/// <summary>装机清单里的一行。</summary>
internal sealed class InstallRow : INotifyPropertyChanged
{
    public InstallRow(InstallApp app)
    {
        App = app;
        StateText = "…";
    }

    public InstallApp App { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Name => App.Name;
    public string Desc => App.Desc;
    public string Id => App.Id;
    public string Category => App.Category;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; Raise(nameof(IsChecked)); }
    }

    private string _stateText;
    public string StateText
    {
        get => _stateText;
        set { _stateText = value; Raise(nameof(StateText)); Raise(nameof(StateBrush)); }
    }

    /// <summary>只有「已安装」和「未安装」两种是确定状态，其余算中间态，用中性色。</summary>
    public Brush StateBrush => _stateText switch
    {
        "已安装" => UiTheme.Success,
        "未安装" => UiTheme.Neutral,
        "安装失败" => UiTheme.Critical,
        "安装中…" => UiTheme.Caution,
        "检查中…" => UiTheme.Neutral,
        _ => UiTheme.Neutral,
    };

    public void NotifyBrush() => Raise(nameof(StateBrush));
}

/// <summary>已装应用里的一行。</summary>
internal sealed class AppxRow : INotifyPropertyChanged
{
    public AppxRow(InstalledApp app)
    {
        App = app;
    }

    public InstalledApp App { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Name => App.Name;
    public string PackageFullName => App.PackageFullName;
    public string Version => App.Version;
    public string Source => App.Source;

    /// <summary>系统组件：能列出来（让人知道它存在），但不允许勾选卸载。</summary>
    public bool IsProtected => AppManager.IsProtectedApp(App.Name);

    public bool CanSelect => !IsProtected;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; Raise(nameof(IsChecked)); }
    }
}

public partial class AppPage : Page, IRefreshablePage
{
    private const string AllCategories = "（全部分类）";
    private const string AllSources = "（全部来源）";

    private readonly List<InstallRow> _installRows = new();
    private readonly ObservableCollection<InstallRow> _installView = new();

    private readonly List<AppxRow> _appRows = new();
    private readonly ObservableCollection<AppxRow> _appView = new();

    private readonly List<string> _log = new();
    private bool _busy;
    private bool _appsLoaded;
    private bool _wingetOk;

    /// <summary>页面被导航离开时取消在跑的操作，别让 winget 在后台继续装。</summary>
    private CancellationTokenSource _cts = new();

    /// <summary>已安装状态查一次就够，来回切页面不重复问 winget（每个包要 0.5 秒）。</summary>
    private readonly Dictionary<string, bool> _installedCache = new(StringComparer.OrdinalIgnoreCase);

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    public AppPage()
    {
        InitializeComponent();
        InstallList.ItemsSource = _installView;
        AppList.ItemsSource = _appView;

        SourceCombo.Items.Add(AllSources);
        SourceCombo.Items.Add("当前用户");
        SourceCombo.Items.Add("系统预装");
        SourceCombo.SelectedIndex = 0;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Loaded += (_, _) => ViewportLock.Lock(this);
        Unloaded += (_, _) => ViewportLock.Unlock(this);

        // 切深浅色后状态色要重算，否则还是旧主题的颜色
        UiTheme.ThemeChanged += () =>
        {
            foreach (var row in _installRows) row.NotifyBrush();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_installRows.Count == 0)
            await LoadCatalogAsync();

        // 「已装应用」要跑 PowerShell，比较慢，等用户真的切过去再读
        if (Tabs.SelectedIndex == 1 && !_appsLoaded)
            await LoadAppsAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_busy) return;      // 正在装东西就别取消，让它跑完
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedIndex == 1 && !_appsLoaded && IsLoaded)
            _ = LoadAppsAsync();
    }

    // ------------------------------------------------------------------ 装机清单
    private async Task LoadCatalogAsync()
    {
        StatusLine.Text = "正在检查 winget…";
        _wingetOk = await AppManager.HasWingetAsync();

        _installRows.Clear();
        foreach (var app in AppCatalog.Build())
            _installRows.Add(new InstallRow(app));

        BuildCategories();
        RefreshInstallView();

        if (!_wingetOk)
        {
            foreach (var row in _installRows) row.StateText = "—";
            StatusLine.Text = "没找到 winget，装机清单不可用";
            ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Warning, "没有 winget",
                "系统里没有 App Installer，装机清单装不了软件。到 Microsoft Store 搜索「应用安装程序」装一下即可。");
            return;
        }

        // 一次 winget list 把全部状态取回来（约 2 秒），比逐个包问快一个数量级
        StatusLine.Text = $"共 {_installRows.Count} 款软件，正在检查已装状态…";
        try
        {
            var installed = await AppManager.InstalledIdsAsync(_installRows.Select(r => r.Id), _cts.Token);
            foreach (var row in _installRows)
            {
                bool on = installed.Contains(row.Id);
                _installedCache[row.Id] = on;
                row.StateText = on ? "已安装" : "未安装";
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            foreach (var row in _installRows)
                if (row.StateText == "…") row.StateText = "—";
            Log("检查已装状态失败：" + ex.Message);
        }

        int installedCount = _installRows.Count(r => r.StateText == "已安装");
        RefreshInstallView();
        StatusLine.Text = $"共 {_installRows.Count} 款，已安装 {installedCount} 款";
        Log($"装机清单已载入：{_installRows.Count} 款，本机已装 {installedCount} 款。");
    }

    private void BuildCategories()
    {
        string keep = CategoryCombo.SelectedItem as string ?? AllCategories;
        CategoryCombo.Items.Clear();
        CategoryCombo.Items.Add(AllCategories);
        foreach (var c in _installRows.Select(r => r.Category).Distinct())
            CategoryCombo.Items.Add(c);

        int index = CategoryCombo.Items.IndexOf(keep);
        CategoryCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void InstallFilter_Changed(object sender, RoutedEventArgs e) => RefreshInstallView();
    private void InstallFilter_Changed(object sender, SelectionChangedEventArgs e) => RefreshInstallView();

    private void RefreshInstallView()
    {
        string category = CategoryCombo.SelectedItem as string ?? AllCategories;
        string keyword = InstallSearch.Text.Trim();

        _installView.Clear();
        foreach (var row in _installRows)
        {
            if (category != AllCategories && row.Category != category) continue;
            if (keyword.Length > 0 &&
                !row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !row.Desc.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !row.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            _installView.Add(row);
        }

        InstallCount.Text = $"显示 {_installView.Count} / {_installRows.Count}";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var picked = _installRows.Where(r => r.IsChecked).ToList();
        if (picked.Count == 0)
        {
            ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Informational, "还没勾选", "先勾选要安装的软件，再点「安装选中」。");
            return;
        }

        if (!_wingetOk)
        {
            ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Error, "winget 不可用", "装不了，先装「应用安装程序」。");
            return;
        }

        bool silent = SilentSwitch.IsChecked == true;
        string names = string.Join("、", picked.Take(5).Select(r => r.Name))
                       + (picked.Count > 5 ? $" 等 {picked.Count} 款" : "");

        if (!await ConfirmAsync("确认安装",
                $"将安装：{names}。\n\n"
                + (silent ? "已勾选「静默安装」，能静默的安装包不会弹窗。\n" : "未勾选静默安装，安装过程可能会弹窗。\n")
                + "耗时取决于网速和包大小，中途可以看底部状态。",
                primaryText: "开始安装"))
            return;

        _busy = true;
        int ok = 0, fail = 0, skip = 0;
        var token = _cts.Token;

        try
        {
            foreach (var row in picked)
            {
                if (token.IsCancellationRequested) break;

                if (row.StateText == "已安装")
                {
                    skip++;
                    Log($"跳过 {row.Name}：已经装过了。");
                    continue;
                }

                row.StateText = "安装中…";
                StatusLine.Text = $"正在安装 {row.Name}…";

                var progress = new Progress<string>(line =>
                {
                    if (line.Length == 0) return;
                    _log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
                    StatusLine.Text = line.Length > 70 ? line[..70] : line;
                });

                try
                {
                    var (success, _) = await AppManager.InstallAsync(row.App, silent, progress, token);
                    if (success)
                    {
                        ok++;
                        _installedCache[row.Id] = true;
                        row.StateText = "已安装";
                        Log($"✔ {row.Name} 安装完成。");
                    }
                    else
                    {
                        fail++;
                        row.StateText = "安装失败";
                        Log($"✘ {row.Name} 安装失败，看上面的 winget 输出。");
                    }
                }
                catch (OperationCanceledException)
                {
                    row.StateText = "未安装";
                    break;
                }
                catch (Exception ex)
                {
                    fail++;
                    row.StateText = "安装失败";
                    Log($"✘ {row.Name} 出错：{ex.Message}");
                }
                finally
                {
                    row.IsChecked = false;
                }
            }

            StatusLine.Text = $"安装完成：成功 {ok}，失败 {fail}，跳过 {skip}";
            Log($"本轮结束：成功 {ok}，失败 {fail}，已装跳过 {skip}。");

            if (fail > 0)
                ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Warning, "有软件没装上",
                    $"{fail} 款失败，点「日志」看 winget 的具体报错。");
            else if (ok > 0)
                ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Success, "装完了", $"{ok} 款软件安装完成。");
        }
        finally
        {
            _busy = false;
            TrimLog();
        }
    }

    // ------------------------------------------------------------------ 已装应用
    private async Task LoadAppsAsync()
    {
        if (_busy) return;
        _appsLoaded = true;

        AppEmptyHint.Text = "正在读取已安装应用…";
        AppEmptyHint.Visibility = Visibility.Visible;
        StatusLine.Text = "正在读取已安装应用…";

        try
        {
            var progress = new Progress<string>(line => StatusLine.Text = line);
            var apps = await AppManager.LoadAppsAsync(includeProvisioned: true, progress, _cts.Token);

            _appRows.Clear();
            foreach (var app in apps)
                _appRows.Add(new AppxRow(app));

            RefreshAppView();
            StatusLine.Text = $"已安装 {_appRows.Count} 个应用";
            Log($"已装应用：{_appRows.Count} 个（含系统预装）。");
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "读取已取消";
        }
        catch (Exception ex)
        {
            StatusLine.Text = "读取失败：" + ex.Message;
            Log("读取已装应用失败：" + ex.Message);
        }
        finally
        {
            AppEmptyHint.Text = "没有匹配的应用";
            AppEmptyHint.Visibility = _appView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void AppFilter_Changed(object sender, RoutedEventArgs e) => RefreshAppView();
    private void AppFilter_Changed(object sender, SelectionChangedEventArgs e) => RefreshAppView();

    private void RefreshAppView()
    {
        string source = SourceCombo.SelectedItem as string ?? AllSources;
        string keyword = AppSearch.Text.Trim();

        _appView.Clear();
        foreach (var row in _appRows)
        {
            if (source != AllSources && row.Source != source) continue;
            if (keyword.Length > 0 &&
                !row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !row.PackageFullName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            _appView.Add(row);
        }

        AppCount.Text = $"显示 {_appView.Count} / {_appRows.Count}";
        AppEmptyHint.Visibility = _appView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // 系统组件即使在别处被程序化勾上也不卸（界面上勾选框是禁用的，这里是第二道闸）
        var picked = _appRows.Where(r => r.IsChecked && !r.IsProtected).ToList();
        if (picked.Count == 0)
        {
            ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Informational, "还没勾选",
                "先勾选要卸载的应用，再点「卸载选中」。带「系统组件」标记的不能卸。");
            return;
        }

        string names = string.Join("、", picked.Take(5).Select(r => r.Name))
                       + (picked.Count > 5 ? $" 等 {picked.Count} 个" : "");

        if (!await ConfirmAsync("确认卸载",
                $"将卸载：{names}。\n\n"
                + "当前用户的应用只影响这个账户；「系统预装」的包卸载后，新建用户也不会再自动装上。\n"
                + "卸掉的商店应用以后可以在 Microsoft Store 里重新装回来。",
                primaryText: "卸载"))
            return;

        _busy = true;
        int ok = 0, fail = 0;

        try
        {
            foreach (var row in picked)
            {
                if (_cts.IsCancellationRequested) break;

                StatusLine.Text = $"正在卸载 {row.Name}…";
                try
                {
                    var (success, output) = await AppManager.RemoveAppAsync(row.App, null, _cts.Token);
                    if (success)
                    {
                        ok++;
                        Log($"✔ 已卸载 {row.Name}");
                        _appRows.Remove(row);
                    }
                    else
                    {
                        fail++;
                        Log($"✘ 卸载 {row.Name} 失败：{Tail(output)}");
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    Log($"✘ 卸载 {row.Name} 出错：{ex.Message}");
                }
            }

            RefreshAppView();
            StatusLine.Text = $"卸载完成：成功 {ok}，失败 {fail}";
            ShowToast(fail > 0 ? Wpf.Ui.Controls.InfoBarSeverity.Warning : Wpf.Ui.Controls.InfoBarSeverity.Success,
                "卸载结束", $"成功 {ok} 个，失败 {fail} 个。");
        }
        finally
        {
            _busy = false;
            TrimLog();
        }
    }

    // ------------------------------------------------------------------ 还原点 / 通用
    private async void RestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!await ConfirmAsync("创建系统还原点",
                "会在当前系统状态上打一个还原点，之后装坏了可以整体回滚。\n\n"
                + "注意：Windows 限制 24 小时内只建一个还原点；系统还原被关掉的话，这一步会自动帮你打开。",
                primaryText: "创建"))
            return;

        _busy = true;
        StatusLine.Text = "正在创建还原点，可能要几十秒…";
        try
        {
            var (success, message) = await AppManager.CreateRestorePointAsync("Win11 工具箱 - 应用管理前");
            Log((success ? "✔ " : "✘ ") + message);
            ShowToast(success ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Warning,
                success ? "还原点已创建" : "没能创建还原点", message);
            StatusLine.Text = message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _installedCache.Clear();
        _appsLoaded = false;

        if (Tabs.SelectedIndex == 1)
        {
            _appRows.Clear();
            _appView.Clear();
            await LoadAppsAsync();
        }
        else
        {
            _installRows.Clear();
            await LoadCatalogAsync();
        }
    }

    private void Log(string message)
    {
        UiThread.RunSafe(StatusLine, () =>
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            TrimLog();
            StatusLine.Text = message;
        });
    }

    private void TrimLog()
    {
        if (_log.Count > 2000) _log.RemoveRange(0, _log.Count - 2000);
    }

    private static string Tail(string text)
    {
        var line = text.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 120 ? line[..120] : line;
    }

    private async void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        var text = _log.Count == 0 ? "（还没有操作记录）" : string.Join(Environment.NewLine, _log);
        try
        {
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "操作日志",
                Content = text,
                ShowTitle = true,
                CloseButtonText = "关闭",
            };
            await dialog.ShowDialogAsync();
        }
        catch { }
    }

    private void ShowToast(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        Toast.Severity = severity;
        Toast.Title = title;
        Toast.Message = message;
        Toast.IsOpen = true;

        _toastTimer?.Stop();
        _toastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(severity == Wpf.Ui.Controls.InfoBarSeverity.Error ? 9 : 5),
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            Toast.IsOpen = false;
        };
        _toastTimer.Start();
    }

    private static async Task<bool> ConfirmAsync(string title, string message, string primaryText = "确定")
    {
        try
        {
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                ShowTitle = true,
                PrimaryButtonText = primaryText,
                CloseButtonText = "取消",
            };
            return await dialog.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ F5 / Ctrl+F
    public void RefreshPage() => Refresh_Click(this, new RoutedEventArgs());

    public void FocusSearch()
    {
        if (Tabs.SelectedIndex == 1)
        {
            AppSearch.Focus();
            AppSearch.SelectAll();
        }
        else
        {
            InstallSearch.Focus();
            InstallSearch.SelectAll();
        }
    }
}
