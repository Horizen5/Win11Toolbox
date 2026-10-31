using System;
using Win11Toolbox.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Win11Toolbox.RightMenu;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Win11Toolbox.Pages;

/// <summary>列表一行。图标和勾选都要能刷新，所以走 INotifyPropertyChanged。</summary>
internal sealed class MenuRow : INotifyPropertyChanged
{
    public MenuRow(RightMenuItem model) => Model = model;

    public RightMenuItem Model { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool Checked
    {
        get => Model.Checked;
        set
        {
            if (Model.Checked == value) return;
            Model.Checked = value;
            Raise(nameof(Checked));
        }
    }

    private ImageSource? _appIcon;
    public ImageSource? AppIcon
    {
        get => _appIcon;
        set { _appIcon = value; Raise(nameof(AppIcon)); }
    }

    public string Title => Model.Title;
    public string Category => Model.Category;
    public string Status => Model.StatusText;
    public string Detail => Model.Detail;
    public string AppName => string.IsNullOrEmpty(Model.AppName) ? "—" : Model.AppName;

    // 画刷做成属性而不是静态字段：TryFindResource 每次返回的都是主题字典里的同一个实例，
    // 不会每次 new 出新对象（滚动时绑定不会误判成"值变了"），
    // 但切换深浅色后能立刻拿到新主题的颜色。
    private static Brush BrushHistory => UiTheme.Caution;    // 已禁用：橙
    private static Brush BrushBlocked => UiTheme.Attention;  // 已屏蔽：蓝
    private static Brush BrushSystem => UiTheme.Neutral;     // 系统自带：灰
    private static Brush BrushNormal => UiTheme.Success;     // 正常：绿

    internal static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public Brush StatusBrush
    {
        get
        {
            if (Model.IsHistory) return BrushHistory;
            if (Model.IsBlocked) return BrushBlocked;
            if (Model.IsSystem) return BrushSystem;
            return BrushNormal;
        }
    }

    /// <summary>鼠标悬停时显示的完整信息。</summary>
    public string Tip
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("菜单名称：" + Model.DisplayName);
            sb.AppendLine("归属软件：" + (string.IsNullOrEmpty(Model.AppName) ? "(未识别)" : Model.AppName));
            sb.AppendLine("出现位置：" + Model.Category);
            sb.AppendLine("当前状态：" + Model.StatusText);
            sb.AppendLine("项目类型：" + RightMenuActions.KindText(Model.Kind));
            if (!string.IsNullOrEmpty(Model.Clsid)) sb.AppendLine("CLSID：" + Model.Clsid);
            if (Model.IsHistory && !string.IsNullOrEmpty(Model.DisabledAt))
                sb.AppendLine("禁用时间：" + Model.DisabledAt);
            sb.AppendLine("注册位置：" + Model.FullRegPath);
            string file = RightMenuActions.FileOf(Model);
            if (file.Length > 0) sb.AppendLine("关联文件：" + file);
            if (Model.MenuNames is { Count: > 1 })
                sb.AppendLine("菜单里还可能叫：" + DllText.Join(Model.MenuNames, 6));
            sb.AppendLine("详细信息：" + Model.Detail);
            sb.Append("（右键这一行有更多操作）");
            return sb.ToString();
        }
    }
}

/// <summary>「按软件」下拉框的选项。</summary>
internal sealed class AppOption
{
    public AppOption(string name, string text) { Name = name; Text = text; }
    public string Name { get; }
    private string Text { get; }
    public override string ToString() => Text;
}

public partial class RightMenuPage : Page, IRefreshablePage
{
    private const string AllApps = "（全部软件）";
    private const string NoApp = "（未识别归属）";

    private readonly List<RightMenuItem> _all = new();
    private readonly ObservableCollection<MenuRow> _rows = new();
    private readonly List<string> _log = new();

    private RightMenuItem? _menuTarget;
    private bool _rebuildingApps;

    public RightMenuPage()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        Loaded += OnLoaded;

        // 切深浅色后把列表重绑一遍：行的状态色是算出来的属性，
        // 不重绑就会一直停在旧主题的颜色上（深色底上的深绿看不见）。
        UiTheme.ThemeChanged += () => UiTheme.Time("RightMenu", RefreshList);

        Loaded += (_, _) => ViewportLock.Lock(this);
        Unloaded += (_, _) => ViewportLock.Unlock(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AdminBarHost.Visibility = RightMenuActions.IsAdmin() ? Visibility.Collapsed : Visibility.Visible;

        if (_all.Count == 0)
        {
            Log("Win11 工具箱 · 右键菜单管理已就绪。");
            await RescanAsync();
        }
    }

    // ------------------------------------------------------------------ 日志 / 状态
    /// <summary>写日志 / 状态栏。可能被后台线程回调（Apply / Restore 在 Task.Run 里跑），要切回 UI 线程。</summary>
    private void Log(string message)
    {
        UiThread.RunSafe(StatusLine, () =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _log.Add(line);
            if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
            StatusLine.Text = message;
        });
    }

    private async void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        var text = _log.Count == 0 ? "（还没有操作记录）" : string.Join(Environment.NewLine, _log);
        await ShowAsync("操作日志", text);
    }

    // ------------------------------------------------------------------ 扫描
    // ------------------------------------------------------------------ F5 / Ctrl+F
    public void RefreshPage()
    {
        if (_rebuildingApps) return;
        _ = RescanAsync();
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async Task RescanAsync()
    {
        StatusLine.Text = "正在扫描注册表…";
        try
        {
            var (list, logs) = await Task.Run(() =>
            {
                var lines = new List<string>();
                var items = Scanner.Scan();
                int imported = 0;
                try { imported = Scanner.ImportHistoryFromBackups(AppPaths.ExeDir, AppPaths.BackupRoot); }
                catch (Exception ex) { lines.Add("整理备份目录出错：" + ex.Message); }
                if (imported > 0) lines.Add($"从 backups 目录补充了 {imported} 条禁用记录。");

                int cleaned = 0, added = 0;
                try { added = Scanner.MergeDisabledHistory(items, AppPaths.ExeDir, out cleaned); }
                catch (Exception ex) { lines.Add("读取禁用记录出错：" + ex.Message); }
                if (cleaned > 0) lines.Add($"有 {cleaned} 项已经回到注册表，对应的禁用记录已清理。");
                if (added > 0) lines.Add($"另有 {added} 项以前禁用过，仍以「已禁用」显示（可随时恢复）。");
                return (items, lines);
            });

            _all.Clear();
            _all.AddRange(list);
            foreach (var line in logs) Log(line);
            Log($"扫描完成，共发现 {_all.Count} 个右键菜单相关项。");

            RebuildAppList();
            RefreshList();
            _ = LoadIconsAsync();
        }
        catch (Exception ex)
        {
            Log("扫描出错：" + ex.Message);
        }
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await RescanAsync();

    // ------------------------------------------------------------------ 过滤 + 渲染
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_rebuildingApps) return;
        if (sender is CheckBox && (sender == ThirdOnly || sender == ShowBlocked))
            RebuildAppList();
        RefreshList();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingApps) return;
        RefreshList();
    }

    private string SelectedApp()
    {
        if (AppCombo.SelectedItem is not AppOption option) return "";
        return option.Name == AllApps ? "" : option.Name;
    }

    private static bool AppMatches(RightMenuItem mi, string app)
    {
        if (string.IsNullOrEmpty(app)) return true;
        if (string.IsNullOrEmpty(mi.AppName)) return app == NoApp;
        return string.Equals(mi.AppName, app, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildAppList()
    {
        string keep = SelectedApp();
        bool thirdOnly = ThirdOnly.IsChecked == true;
        bool showBlocked = ShowBlocked.IsChecked == true;

        var tally = new SortedDictionary<string, int>(StringComparer.CurrentCulture);
        int noApp = 0;
        foreach (var mi in _all)
        {
            if (thirdOnly && mi.IsSystem) continue;
            if (mi.IsBlocked && !showBlocked) continue;
            if (string.IsNullOrEmpty(mi.AppName)) { noApp++; continue; }
            tally.TryGetValue(mi.AppName, out int n);
            tally[mi.AppName] = n + 1;
        }

        _rebuildingApps = true;
        try
        {
            AppCombo.Items.Clear();
            int universe = tally.Values.Sum() + noApp;
            AppCombo.Items.Add(new AppOption(AllApps, $"{AllApps}   共 {universe} 项"));
            foreach (var kv in tally)
                AppCombo.Items.Add(new AppOption(kv.Key, $"{kv.Key}   ({kv.Value})"));
            if (noApp > 0)
                AppCombo.Items.Add(new AppOption(NoApp, $"{NoApp}   ({noApp})"));

            int sel = 0;
            if (keep.Length > 0)
            {
                for (int i = 1; i < AppCombo.Items.Count; i++)
                {
                    if (AppCombo.Items[i] is AppOption o && o.Name == keep) { sel = i; break; }
                }
            }
            AppCombo.SelectedIndex = sel;
        }
        finally { _rebuildingApps = false; }
    }

    private void RefreshList()
    {
        string keyword = SearchBox.Text.Trim();
        string app = SelectedApp();
        bool thirdOnly = ThirdOnly.IsChecked == true;
        bool showBlocked = ShowBlocked.IsChecked == true;
        bool showHistory = ShowHistory.IsChecked == true;
        bool showIcons = ShowIcons.IsChecked == true;

        var rows = new List<MenuRow>();
        int third = 0;

        foreach (var mi in _all)
        {
            if (thirdOnly && mi.IsSystem) continue;
            if (mi.IsBlocked && !showBlocked) continue;
            if (mi.IsHistory && !showHistory) continue;
            if (!AppMatches(mi, app)) continue;
            if (keyword.Length > 0)
            {
                bool hit = mi.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                           || mi.Category.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                           || mi.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                           || mi.FullPath.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                           || (mi.AppName ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;
            }

            var row = new MenuRow(mi);
            if (showIcons) row.AppIcon = MenuIconProvider.Get(mi);
            rows.Add(row);
            if (!mi.IsSystem) third++;
        }

        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);

        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountHint.Text = $"显示 {rows.Count} / 共 {_all.Count} 项（第三方 {third} 项）";
    }

    /// <summary>图标解析要读 DLL 资源，放到后台算，算完再一次性填回列表。</summary>
    private async Task LoadIconsAsync()
    {
        if (ShowIcons.IsChecked != true) return;
        var snapshot = _rows.Where(r => r.AppIcon == null).ToList();
        if (snapshot.Count == 0) return;

        var map = await Task.Run(() =>
        {
            var result = new List<(MenuRow Row, ImageSource? Icon)>(snapshot.Count);
            foreach (var row in snapshot)
            {
                ImageSource? icon = null;
                try { icon = MenuIconProvider.Get(row.Model); } catch { }
                result.Add((row, icon));
            }
            return result;
        });

        foreach (var (row, icon) in map)
            if (_rows.Contains(row)) row.AppIcon = icon;
    }

    // ------------------------------------------------------------------ 勾选
    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.ToggleButton { DataContext: MenuRow row } toggle)
            row.Checked = toggle.IsChecked == true;
    }

    private void SelectAllThird_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var row in _rows)
        {
            if (row.Model.IsSystem || row.Model.IsBlocked || row.Model.IsHistory) continue;
            row.Checked = true;
            n++;
        }
        Log($"已勾选当前列表里的 {n} 个第三方项。");
    }

    private void ClearChecked_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Checked = false;
        Log("已清除全部勾选。");
    }

    private List<RightMenuItem> CheckedItems() => _all.Where(m => m.Checked).ToList();

    // ------------------------------------------------------------------ 备份并禁用 / 恢复
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var sel = CheckedItems();
        if (sel.Count == 0)
        {
            await ShowAsync("没有勾选项目", "请先在列表里勾选要隐藏的右键菜单项。");
            return;
        }

        int needAdmin = sel.Count(m => m.Root == "HKLM" && !m.IsBlocked && !m.IsHistory);
        if (needAdmin > 0 && !RightMenuActions.IsAdmin())
        {
            bool go = await ConfirmAsync("需要管理员权限",
                $"选中的项目中有 {needAdmin} 项位于 HKLM，需要管理员权限。\n是否以管理员身份重新启动？");
            if (go && RightMenuActions.Elevate()) Application.Current.Shutdown();
            return;
        }

        var preview = string.Join("\n", sel.Take(12).Select(m => $"  · [{m.Category}] {m.DisplayName}"));
        if (sel.Count > 12) preview += $"\n  … 其余 {sel.Count - 12} 项";
        if (!await ConfirmAsync("确认禁用",
                $"即将禁用以下 {sel.Count} 项：\n\n{preview}\n\n会先自动生成 .reg 备份，可随时还原。是否继续？"))
            return;

        StatusLine.Text = "正在备份并禁用…";

        ActionReport? report = null;
        try
        {
            report = await Task.Run(() => RightMenuActions.Apply(sel, Log));
        }
        catch (Exception ex)
        {
            // 中途出错也必须重扫：注册表可能已经改了一半，列表还停在旧状态，
            // 用户会以为"禁掉了但列表里找不到"（这正是 ToDesk 那次的表现）。
            Log("禁用过程出错：" + ex.Message);
        }
        finally
        {
            await RescanAsync();
        }

        if (report == null)
        {
            await ShowAsync("操作没有完成",
                "处理过程中出错，列表已经按注册表的真实状态重新读取。\n\n" +
                "已经改动的项会以「已禁用」出现在列表里，勾选后点「恢复选中项」即可还原。\n\n" +
                "详细原因见「日志」。");
            return;
        }

        Log("完成：" + report.Summary + "　备份：" + report.BackupDir);

        if (report.Ok > 0 && await ConfirmAsync("完成",
                $"成功 {report.Ok} 项，失败 {report.Fail} 项。\n\n备份目录：\n{report.BackupDir}\n\n是否现在重启资源管理器让改动立即生效？"))
        {
            await Task.Run(RightMenuActions.RestartExplorer);
            Log("资源管理器已重启。");
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var sel = CheckedItems();
        var blocked = sel.Where(m => m.IsBlocked).ToList();
        var history = sel.Where(m => m.IsHistory).ToList();

        if (blocked.Count == 0 && history.Count == 0)
        {
            await ShowAsync("没有可恢复的项",
                "请勾选状态为「已屏蔽」或「已禁用」的项目。\n\n" +
                "· 已屏蔽 = 被 Windows 官方屏蔽列表挡住的外壳扩展（蓝色）\n" +
                "· 已禁用 = 以前被禁用、记录在 disabled.tsv 里的传统菜单项（棕橙色）");
            return;
        }

        string question = "即将恢复：\n\n";
        if (blocked.Count > 0) question += $"  解除屏蔽　　{blocked.Count} 项\n";
        if (history.Count > 0) question += $"  按备份还原　{history.Count} 项\n";
        question += "\n确定继续？";
        if (!await ConfirmAsync("确认恢复", question)) return;

        var report = await Task.Run(() => RightMenuActions.Restore(sel, Log));
        Log("恢复完成：" + report.Summary);
        await RescanAsync();
    }

    private async void RestoreFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择备份的 .reg 文件",
            Filter = "注册表备份 (*.reg)|*.reg|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(AppPaths.BackupRoot) ? AppPaths.BackupRoot : AppPaths.ExeDir,
        };
        if (dialog.ShowDialog() != true) return;

        if (!await ConfirmAsync("确认恢复", $"即将导入：\n{dialog.FileName}\n\n确定继续？")) return;

        var (applied, fail) = await Task.Run(() =>
        {
            int n = RightMenuActions.RestoreFromFile(dialog.FileName, out int bad, Log);
            return (n, bad);
        });
        await RescanAsync();
        await ShowAsync("恢复完成", $"应用 {applied} 处改动，失败 {fail} 处。");
    }

    private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("重启资源管理器", "桌面和任务栏会消失两三秒后自动恢复，是否继续？")) return;
        await Task.Run(RightMenuActions.RestartExplorer);
        Log("资源管理器已重启。");
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.BackupRoot);
            RightMenuActions.OpenDirectory(AppPaths.BackupRoot);
        }
        catch (Exception ex) { Log("打开备份目录失败：" + ex.Message); }
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (RightMenuActions.Elevate()) Application.Current.Shutdown();
        else Log("提权失败（可能是在 UAC 里点了「否」）。");
    }

    // ------------------------------------------------------------------ 行右键菜单
    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        _menuTarget = (menu.PlacementTarget as FrameworkElement)?.DataContext is MenuRow row ? row.Model : null;
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null) return;
        string file = RightMenuActions.FileOf(_menuTarget);
        if (file.Length == 0)
        {
            await ShowAsync("没有关联文件", "这一项没有关联到具体文件。\n可以改用「在注册表编辑器中定位」看它注册在哪。");
            return;
        }
        try { RightMenuActions.OpenFileLocation(file); Log("已定位：" + file); }
        catch (Exception ex) { Log("打开失败：" + ex.Message); }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null) return;
        string file = RightMenuActions.FileOf(_menuTarget);
        if (file.Length == 0) return;
        try
        {
            string dir = Path.GetDirectoryName(file) ?? "";
            if (dir.Length > 0) { RightMenuActions.OpenDirectory(dir); Log("已打开目录：" + dir); }
        }
        catch (Exception ex) { Log("打开失败：" + ex.Message); }
    }

    private void OpenRegedit_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null) return;
        try { RightMenuActions.OpenInRegedit(_menuTarget.FullRegPath); Log("注册表定位：" + _menuTarget.FullRegPath); }
        catch (Exception ex) { Log("打开注册表编辑器失败：" + ex.Message); }
    }

    private void CopyRegPath_Click(object sender, RoutedEventArgs e) => Copy(_menuTarget?.FullRegPath, "注册表路径");
    private void CopyName_Click(object sender, RoutedEventArgs e) => Copy(_menuTarget?.DisplayName, "菜单名称");
    private void CopyDetail_Click(object sender, RoutedEventArgs e) => Copy(_menuTarget?.Detail, "详细信息");

    private void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Log("已复制" + what + "：" + text); }
        catch (Exception ex) { Log("复制失败：" + ex.Message); }
    }

    private void OnlyThisApp_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null) return;
        string want = string.IsNullOrEmpty(_menuTarget.AppName) ? NoApp : _menuTarget.AppName;

        if (!SelectAppInCombo(want))
        {
            if (ThirdOnly.IsChecked == true) ThirdOnly.IsChecked = false;
            RebuildAppList();
            if (!SelectAppInCombo(want))
            {
                Log("列表里找不到归属为「" + want + "」的项目。");
                return;
            }
        }
        Log("只看这个软件：" + want);
    }

    private bool SelectAppInCombo(string want)
    {
        for (int i = 0; i < AppCombo.Items.Count; i++)
        {
            if (AppCombo.Items[i] is AppOption o && string.Equals(o.Name, want, StringComparison.OrdinalIgnoreCase))
            {
                AppCombo.SelectedIndex = i;
                return true;
            }
        }
        return false;
    }

    private void SelectSameApp_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null) return;
        string want = _menuTarget.AppName;
        string label = string.IsNullOrEmpty(want) ? "未识别归属" : want;

        int n = 0;
        foreach (var row in _rows)
        {
            var x = row.Model;
            if (x.IsSystem || x.IsBlocked) continue;
            bool same = string.IsNullOrEmpty(want)
                ? string.IsNullOrEmpty(x.AppName)
                : string.Equals(x.AppName, want, StringComparison.OrdinalIgnoreCase);
            if (!same) continue;
            row.Checked = true;
            n++;
        }
        Log($"已在当前列表勾选「{label}」的 {n} 项。");
    }

    private async void RestoreOne_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null || !_menuTarget.IsHistory) return;
        if (!await ConfirmAsync("确认恢复",
                $"确定把「{_menuTarget.DisplayName}」恢复回右键菜单吗？\n\n" +
                (_menuTarget.BackupItemReg.Length > 0
                    ? "将导入当时的备份：\n" + _menuTarget.BackupItemReg
                    : "（这一项没有单项备份，无法精确恢复）")))
            return;

        var target = _menuTarget;
        if (await Task.Run(() => RightMenuActions.RestoreOneHistory(target, true, Log)))
            await RescanAsync();
    }

    private void ExportOne_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "单独备份这一项为 .reg",
            Filter = "注册表备份 (*.reg)|*.reg|所有文件 (*.*)|*.*",
            FileName = RightMenuActions.MakeSafeName(_menuTarget.DisplayName) + ".reg",
            InitialDirectory = Directory.Exists(AppPaths.BackupRoot) ? AppPaths.BackupRoot : AppPaths.ExeDir,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            RightMenuActions.ExportOne(_menuTarget, dialog.FileName);
            Log("已单独备份：" + dialog.FileName);
        }
        catch (Exception ex) { Log("备份失败：" + ex.Message); }
    }

    private async void ShowInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_menuTarget is null) return;
        var mi = _menuTarget;
        string file = RightMenuActions.FileOf(mi);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("菜单名称　" + mi.DisplayName);
        sb.AppendLine("归属软件　" + (string.IsNullOrEmpty(mi.AppName) ? "(未识别)" : mi.AppName));
        sb.AppendLine("出现位置　" + mi.Category);
        sb.AppendLine("当前状态　" + mi.StatusText);
        sb.AppendLine("项目类型　" + RightMenuActions.KindText(mi.Kind));
        if (mi.Clsid.Length > 0) sb.AppendLine("CLSID　　　" + mi.Clsid);
        sb.AppendLine("注册位置　" + mi.FullRegPath);
        sb.AppendLine("关联文件　" + (file.Length > 0 ? file : "(无)"));
        sb.AppendLine("图标来源　" + (string.IsNullOrEmpty(mi.IconSpec) ? "(无)" : mi.IconSpec));
        if (mi.MenuNames is { Count: > 1 })
            sb.AppendLine("菜单文字候选　" + DllText.Join(mi.MenuNames, 6));
        if (mi.IsHistory)
        {
            sb.AppendLine("禁用时间　" + mi.DisabledAt);
            sb.AppendLine("单项备份　" + (mi.BackupItemReg.Length > 0 ? mi.BackupItemReg : "(无)"));
        }
        sb.Append("详细信息　" + mi.Detail);

        await ShowAsync("完整信息 — " + mi.DisplayName, sb.ToString());
    }

    // ------------------------------------------------------------------ 对话框
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
        catch { return false; }
    }

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
