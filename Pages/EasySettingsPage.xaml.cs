using System;
using Win11Toolbox.Controls;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Win11Toolbox.RightMenu;
using Win11Toolbox.Services;
using Win11Toolbox.SettingsKit;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Win11Toolbox.Pages;

/// <summary>一个设置项在界面上的行。</summary>
internal sealed class SettingRow : INotifyPropertyChanged
{
    private bool _suppress;

    public SettingRow(SettingItem model)
    {
        Model = model;
        StateText = "…";
    }

    public SettingItem Model { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Name => Model.Name;
    public string Desc => Model.Desc;
    public bool NeedAdmin => Model.NeedAdmin;
    public bool NeedExplorerRestart => Model.NeedExplorerRestart;
    public bool Danger => Model.Danger;

    private bool _isOn;
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            Raise(nameof(IsOn));
            if (!_suppress) Changed?.Invoke(this, value);
        }
    }

    /// <summary>勾选状态变化（用户在界面上点的）。</summary>
    public event Action<SettingRow, bool>? Changed;

    private string _stateText = "";
    // 状态色走主题语义色，深浅色自动切换（写死的深绿在深色底上看不见）
    private static Brush BrushOn => UiTheme.Success;
    private static Brush BrushOff => UiTheme.Neutral;
    private static Brush BrushError => UiTheme.Critical;
    private static Brush BrushWarn => UiTheme.Caution;

    public string StateText
    {
        get => _stateText;
        set { _stateText = value; Raise(nameof(StateText)); Raise(nameof(StateBrush)); }
    }

    public Brush StateBrush => _stateText switch
    {
        "已开启" => BrushOn,
        "默认状态" => BrushOff,
        "待重启生效" => BrushWarn,
        "未生效" => BrushError,
        "写入失败" => BrushError,
        _ => BrushOff,
    };

    public string Tip
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Model.Name);
            sb.AppendLine(Model.Desc);
            if (Model.Danger) sb.AppendLine("⚠ 谨慎项：" + Model.Risk);
            if (!string.IsNullOrEmpty(Model.RegPath)) sb.AppendLine("注册表：" + Model.RegPath);
            sb.AppendLine("需要管理员：" + (Model.NeedAdmin ? "是" : "否"));
            if (Model.NeedExplorerRestart) sb.AppendLine("需要重启资源管理器才完全生效");
            sb.Append("取消勾选 = 恢复 Windows 默认。");
            return sb.ToString();
        }
    }

    /// <summary>不触发 Changed 事件地设置勾选（刷新探测结果时用）。</summary>
    public void SetSilently(bool on)
    {
        _suppress = true;
        try { IsOn = on; }
        finally { _suppress = false; }
    }
}

public partial class EasySettingsPage : Page, IRefreshablePage
{
    private const string AllCategories = "（全部类别）";

    private readonly List<SettingItem> _all = new();
    private readonly List<SettingRow> _allRows = new();
    private readonly ObservableCollection<SettingRow> _rows = new();
    private readonly List<string> _log = new();

    /// <summary>电源计划清单。with 表达式要用 List（不能是 IReadOnlyList）。</summary>
    private List<(string Guid, string Name, bool Active)> _plans = new();

    /// <summary>电源计划只读一次：这是每次进页面都要跑的，重复读纯属浪费。</summary>
    private bool _plansLoaded;

    private bool _busy;

    /// <summary>
    /// 写操作串行化。搜索框那一项走「整键还原」，一次要一两秒，
    /// 用户很容易在这期间点第二次 —— 以前直接 return 把点击丢掉，
    /// 结果勾选框变了、注册表没动，看起来就是「勾上了但没生效」。
    /// 现在改成排队 + 合并：同一项连续点只保留最后一次想要的状态。
    /// </summary>
    private readonly SemaphoreSlim _applyLock = new SemaphoreSlim(1, 1);
    private readonly List<(SettingRow Row, bool Want)> _pending = new List<(SettingRow, bool)>();
    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    public EasySettingsPage()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        Loaded += OnLoaded;
        // 切深浅色后重画一遍：状态色是算出来的，不重来就还是旧主题的颜色
        UiTheme.ThemeChanged += () => UiTheme.Time("EasySettings", RefreshList);

        Loaded += (_, _) => ViewportLock.Lock(this);
        Unloaded += (_, _) => ViewportLock.Unlock(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AdminBarHost.Visibility = RightMenuActions.IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
        if (_allRows.Count == 0)
            await LoadAsync();

        await LoadPowerPlansAsync();
    }

    // ------------------------------------------------------------------ 电源计划
    /// <summary>
    /// 电源计划原来只在「优化」页里，太深，用户根本找不到。
    /// 它属于"看一眼就能改的系统开关"，挪到轻松设置页来，和其它设置项放在一起。
    ///
    /// 读的是 <c>powercfg /list</c>，要跑外部进程，所以放后台线程，避免卡 UI。
    /// </summary>
    private async Task LoadPowerPlansAsync()
    {
        if (_plansLoaded) return;
        _plansLoaded = true;

        StatusLine.Text = "正在读取电源计划…";
        var plans = await Task.Run(() => ShellTools.PowerPlans());

        _plans = plans;
        PowerCombo.Items.Clear();
        foreach (var plan in plans)
            PowerCombo.Items.Add(plan.Name);

        var active = plans.FindIndex(p => p.Active);
        PowerCombo.SelectedIndex = active >= 0 ? active : (plans.Count > 0 ? 0 : -1);
        StatusLine.Text = plans.Count > 0 ? $"电源计划 {plans.Count} 个" : "没读到电源计划";
    }

    private void ApplyPower_Click(object sender, RoutedEventArgs e)
    {
        var index = PowerCombo.SelectedIndex;
        if (index < 0 || index >= _plans.Count) return;

        var plan = _plans[index];
        var ok = ShellTools.SetPowerPlan(plan.Guid);

        if (ok)
        {
            for (int i = 0; i < _plans.Count; i++)
                _plans[i] = _plans[i] with { Active = i == index };
        }

        StatusLine.Text = ok
            ? $"已切换到「{plan.Name}」"
            : "切换失败，可能需要管理员权限";
    }

    private void Log(string message)
    {
        UiThread.RunSafe(StatusLine, () =>
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
            StatusLine.Text = message;
        });
    }

    private async void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        var text = _log.Count == 0 ? "（还没有操作记录）" : string.Join(Environment.NewLine, _log);
        await ShowAsync("操作日志", text);
    }

    // ------------------------------------------------------------------ 载入 + 探测
    /// <summary>
    /// 两段式载入。
    ///
    /// 之前是「40 项全部 Probe 完 → 才第一次往 _rows 里塞行」，Probe 要读注册表、
    /// 有的还要跑 powercfg，整段下来好几秒，用户看到的就是一个纯白的空列表。
    ///
    /// 现在：第一段纯内存建行（毫秒级），列表立刻有内容、名字和说明都能看；
    /// 第二段后台分批探测，每批回来就往对应行回填状态，肉眼能看到它一项项亮起来。
    /// </summary>
    private async Task LoadAsync()
    {
        StatusLine.Text = "正在读取设置清单…";

        // 第一段：建清单 + 建行。全部是内存操作，不碰系统。
        var items = EasySettingsData.Build();
        _all.Clear();
        _all.AddRange(items);
        _allRows.Clear();

        foreach (var it in _all)
        {
            var row = new SettingRow(it);      // StateText 初始为 "…"，状态未知
            row.Changed += OnRowChanged;
            _allRows.Add(row);
        }

        BuildCategories();
        RefreshList();
        CountHint.Text = $"共 {_allRows.Count} 项　正在读取当前状态…";

        // 第二段：分批探测并回填。每批 4 项，批与批之间让出 UI 线程。
        const int BatchSize = 4;
        int total = _allRows.Count;
        for (int start = 0; start < total; start += BatchSize)
        {
            int from = start;
            int to = Math.Min(start + BatchSize, total);

            var batch = await Task.Run(() =>
            {
                var result = new (bool On, string Text, bool Ok)[to - from];
                for (int i = from; i < to; i++)
                {
                    var item = _allRows[i].Model;
                    try
                    {
                        bool on = item.Probe();
                        result[i - from] = (on, on ? "已开启" : "默认状态", true);
                    }
                    catch
                    {
                        result[i - from] = (false, "读取失败", false);
                    }
                }
                return result;
            });

            for (int i = 0; i < batch.Length; i++)
                ApplyProbe(_allRows[from + i], batch[i]);

            CountHint.Text = $"共 {total} 项　已读取 {to} 项…";
        }

        int onCount = _allRows.Count(r => r.IsOn);
        CountHint.Text = $"显示 {_rows.Count} / 共 {_allRows.Count} 项　已开启 {onCount} 项";
        Log($"已载入 {_allRows.Count} 项设置，当前开启 {onCount} 项。");
    }

    /// <summary>把一条探测结果回填到行上。用户已经自己动过的行不覆盖。</summary>
    private static void ApplyProbe(SettingRow row, (bool On, string Text, bool Ok) result)
    {
        if (row.StateText != "…") return;      // 已经有结论了（用户点过 / 之前填过），不覆盖

        row.Model.StateText = result.Text;
        row.Model.StateOk = result.Ok;
        row.SetSilently(result.On);
        row.StateText = result.Text;
    }

    private void BuildCategories()
    {
        string keep = CategoryCombo.SelectedItem as string ?? AllCategories;

        CategoryCombo.Items.Clear();
        CategoryCombo.Items.Add(AllCategories);
        foreach (var c in _all.Select(i => i.Category).Distinct())
            CategoryCombo.Items.Add(c);

        int index = CategoryCombo.Items.IndexOf(keep);
        CategoryCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshList();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RefreshList();

    private void RefreshList()
    {
        string category = CategoryCombo.SelectedItem as string ?? AllCategories;
        string keyword = SearchBox.Text.Trim();

        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (category != AllCategories && row.Model.Category != category) continue;
            if (keyword.Length > 0 &&
                !row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !row.Desc.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !row.Model.RegPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;
            _rows.Add(row);
        }

        CountHint.Text = $"显示 {_rows.Count} / 共 {_allRows.Count} 项　已开启 {_allRows.Count(r => r.IsOn)} 项";
    }

    // ------------------------------------------------------------------ 应用单项
    private async void OnRowChanged(SettingRow row, bool want)
    {
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

    private async void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        // 勾选变化由 SettingRow.Changed 处理；这里只是把点击事件吃掉，避免冒泡到行容器
        await Task.CompletedTask;
    }

    private async Task ApplyOneAsync(SettingRow row, bool want)
    {
        // 系统不让程序改的项（目前只有 Win11 锁死的搜索键）：别硬写，直接把人送到系统设置。
        // 硬写的下场是弹一句「写入失败 / 拒绝访问」，用户既不知道原因也不知道下一步干什么。
        if (want && row.Model.GuideUrl.Length > 0)
        {
            bool go = await ConfirmAsync("这一项得去系统设置里改",
                row.Model.Desc + "\n\n点「打开设置」会跳到「设置 → 个性化 → 任务栏」，"
                + "在「搜索」那一行选「隐藏」就行。改完回来点「刷新状态」就能对上。",
                primaryText: "打开设置");
            row.SetSilently(false);
            RefreshList();
            if (go) EasySettingsData.OpenLink(row.Model.GuideUrl);
            return;
        }

        // 危险项在「打开」之前问一句；取消勾选是恢复默认，不打扰
        if (want && row.Model.Danger)
        {
            bool go = await ConfirmAsync("确认开启：" + row.Name,
                row.Model.Risk + "\n\n以后想还原，把这一项取消勾选就行。",
                primaryText: "仍然开启");
            if (!go)
            {
                row.SetSilently(false);
                RefreshList();
                return;
            }
        }

        _busy = true;
        StatusLine.Text = (want ? "正在应用 " : "正在恢复 ") + row.Name + "…";
        try
        {
            var (text, ok, error) = await Task.Run<(string Text, bool Ok, string Error)>(() =>
            {
                try
                {
                    row.Model.Set(want);

                    // 写后回读：写完再探一次，看真实状态到底变没变
                    bool now = row.Model.Probe();

                    if (now == want)
                        return (now ? "已开启" : "默认状态", true, "");

                    // 想关但读回来还是开着：写进去了，只是要等资源管理器/重启才反映出来，
                    // 这种情况不该判失败，也不能把勾弹回去让用户以为没生效。
                    if (!want)
                        return ("待重启生效", true, "");

                    return ("未生效", false, "写入后读回来仍是「默认状态」");
                }
                catch (Exception ex)
                {
                    return ("写入失败", false, ex.Message);
                }
            });

            row.Model.StateText = text;
            row.Model.StateOk = ok;
            row.StateText = text;

            // 界面上的勾选状态跟真实状态对齐：真失败就弹回去，别骗人
            if (!ok) row.SetSilently(!want);

            if (ok)
            {
                Log((want ? "已应用 " : "已恢复默认 ") + row.Name + "（当前：" + text + "）"
                    + (row.NeedExplorerRestart || text == "待重启生效" ? "　⟳ 需重启资源管理器后生效" : ""));

                if (text == "待重启生效")
                    ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                        row.Name + " 已写入",
                        "系统状态要等重启资源管理器（或重启电脑）后才会变过来。");
            }
            else
            {
                Log((want ? "应用 " : "恢复 ") + row.Name + " 失败：" + error);

                bool admin = RightMenuActions.IsAdmin();
                string hint = !admin && row.NeedAdmin
                    ? "这一项需要管理员权限，请点左下角「以管理员身份重启」后再试。"
                    : "可能是系统策略锁定了这个值，或该功能在当前 Windows 版本上不可用。";
                ShowToast(Wpf.Ui.Controls.InfoBarSeverity.Error,
                    row.Name + " " + text, hint + (error.Length > 0 ? "\n" + error : ""));
            }
        }
        finally
        {
            _busy = false;
            RefreshList();
        }
    }

    // ------------------------------------------------------------------ 批量
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        StatusLine.Text = "正在重新读取状态…";
        try
        {
            // 只把"读注册表"放后台；模型上的 StateText / StateOk 会触发 PropertyChanged，
            // 必须在 UI 线程改，否则绑定层会在后台线程去动 TextBlock 直接抛异常。
            var states = await Task.Run(() =>
            {
                var result = new List<(bool Ok, bool On)>(_allRows.Count);
                foreach (var row in _allRows)
                {
                    try { result.Add((true, row.Model.Probe())); }
                    catch { result.Add((false, false)); }
                }
                return result;
            });

            for (int i = 0; i < _allRows.Count && i < states.Count; i++)
            {
                var row = _allRows[i];
                var (probed, on) = states[i];
                row.Model.StateOk = probed;
                if (probed)
                    row.Model.StateText = on ? "已开启" : "默认状态";
                row.SetSilently(row.Model.StateOk && row.Model.StateText == "已开启");
                row.StateText = row.Model.StateOk ? row.Model.StateText : "读取失败";
            }
            Log("状态已刷新。");
        }
        finally
        {
            _busy = false;
            RefreshList();
        }
    }

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!await ConfirmAsync("全部恢复默认",
                $"将把全部 {_allRows.Count} 项设置恢复为 Windows 默认值。\n确定继续吗？"))
            return;

        _busy = true;
        StatusLine.Text = "正在全部恢复默认…";
        int ok = 0, bad = 0;
        try
        {
            foreach (var row in _allRows)
            {
                try
                {
                    // 同样：写注册表放后台，模型属性回 UI 线程再改
                    bool now = await Task.Run(() =>
                    {
                        row.Model.Set(false);
                        return row.Model.Probe();
                    });
                    row.Model.StateText = now ? "已开启" : "默认状态";
                    row.Model.StateOk = true;
                    row.SetSilently(false);
                    row.StateText = row.Model.StateText;
                    ok++;
                }
                catch (Exception ex)
                {
                    bad++;
                    row.StateText = "写入失败";
                    Log("恢复 " + row.Name + " 失败：" + ex.Message);
                }
            }
            Log($"全部恢复默认完成：成功 {ok} 项，失败 {bad} 项。部分设置需重启资源管理器才完全生效。");
        }
        finally
        {
            _busy = false;
            RefreshList();
        }
    }

    private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("重启资源管理器", "桌面和任务栏会消失两三秒后自动恢复，是否继续？")) return;
        await Task.Run(RightMenuActions.RestartExplorer);
        Log("资源管理器已重启。");
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (RightMenuActions.Elevate()) Application.Current.Shutdown();
        else Log("提权失败（可能是在 UAC 里点了「否」）。");
    }

    // ------------------------------------------------------------------ 对话框
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

    /// <summary>页面内的临时提示条，4 秒后自己收起来。</summary>
    private void ShowToast(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        Toast.Severity = severity;
        Toast.Title = title;
        Toast.Message = message;
        Toast.IsOpen = true;

        _toastTimer?.Stop();
        _toastTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(severity == Wpf.Ui.Controls.InfoBarSeverity.Error ? 9 : 4),
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            Toast.IsOpen = false;
        };
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ F5 / Ctrl+F
    public void RefreshPage()
    {
        if (_busy) return;
        Refresh_Click(this, new RoutedEventArgs());
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
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
