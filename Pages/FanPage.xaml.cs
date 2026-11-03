using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Win11Toolbox.Controls;
using Win11Toolbox.RightMenu;
using Win11Toolbox.Services;

namespace Win11Toolbox.Pages;

/// <summary>
/// 风扇控制：读取转速/温度，手动调速或交回自动。
/// 底层走 LibreHardwareMonitor，写 SuperIO 需要管理员 + PawnIO 驱动。
/// </summary>
public partial class FanPage : Page
{
    private FanController? _controller;
    private DispatcherTimer? _timer;
    private bool _busy;
    private bool _built;
    private string _statusAction = "";
    private readonly Dictionary<string, Tile> _tiles = new(StringComparer.Ordinal);

    // ---- 风扇曲线 ----
    private bool _ready;                  // 构造期间滑块会触发 ValueChanged，此时控件还没齐
    private bool _curveSync;              // 内部改滑块时不要再递归
    private bool _curveEnabled;
    private float _maxTemp;               // 当前最高温度，画图用
    private int _lastApplied = -1;        // 上次真正写进 EC 的百分比，避免每 2 秒重复写
    private DispatcherTimer? _saveTimer;  // 拖滑块时别每个像素都落盘
    private DateTime _verifyHold;         // 校验警告在这之前不被定时刷新覆盖
    private readonly HashSet<string> _ineffective = new(StringComparer.Ordinal);  // 实测写不动的风扇

    public FanPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        LoadCurveSettings();
        _ready = true;
        RedrawCurve();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            // 打开 LHM / 加载驱动可能要几百毫秒，放后台线程别卡 UI
            await Task.Run(() =>
            {
                var controller = new FanController();
                controller.Open();
                _controller = controller;
            });
        }

        await RefreshAsync();
        StartTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _timer = null;
    }

    private void StartTimer()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_controller is null || _busy) return;
        _busy = true;
        try
        {
            // 曲线参数在 UI 线程读好，后台线程只负责采集和写值
            var curveOn = _curveEnabled;
            var start = (int)StartSlider.Value;
            var full = (int)FullSlider.Value;
            var min = (int)MinSlider.Value;
            var last = _lastApplied;

            var (fans, temps) = await Task.Run(() =>
            {
                var (f, t) = _controller.Snapshot();

                if (curveOn)
                {
                    var hottest = t.Where(x => x.Value > 0).Select(x => x.Value).DefaultIfEmpty(0).Max();
                    if (hottest > 0)
                    {
                        var percent = (int)MathF.Round(CurvePercent(start, full, min, hottest));
                        if (percent != last)
                        {
                            foreach (var fan in f.Where(x => x.Controllable))
                                _controller.SetPercent(fan, percent);
                            last = percent;
                        }
                    }
                }

                return (f, t);
            });

            _lastApplied = last;
            _maxTemp = temps.Where(x => x.Value > 0).Select(x => x.Value).DefaultIfEmpty(0).Max();
            Render(fans, temps);
            RedrawCurve();
        }
        catch
        {
            // 单次刷新失败不弹窗，下一轮再试
        }
        finally
        {
            _busy = false;
        }
    }

    private void Render(List<FanChannel> fans, List<TempReading> temps)
    {
        if (!_built || !TilesMatch(fans))
        {
            BuildTiles(fans);
            _built = true;
        }

        // 新建的磁贴也要立刻填上数值，否则第一帧会显示占位符
        SyncTiles(fans);

        RenderTemps(temps);
        UpdateStatus(fans);
    }

    private async Task ReloadAsync()
    {
        if (_controller is null)
        {
            await Task.Run(() =>
            {
                var controller = new FanController();
                controller.Open();
                _controller = controller;
            });
        }

        _built = false;
        _ineffective.Clear();
        _verifyHold = default;
        await RefreshAsync();
    }

    // ------------------------------------------------------------------ 风扇磁贴

    private sealed class Tile
    {
        public Wpf.Ui.Controls.Card Card = null!;
        public FanChannel Channel = null!;
        public TextBlock Rpm = null!;
        public TextBlock Unit = null!;
        public TextBlock Percent = null!;
        public Slider? Slider;
        public Wpf.Ui.Controls.Button? Apply;
        public Canvas? Spark;
        public TextBlock? SparkNote;
        public Border? BarFill;
        public bool Suppress;

        /// <summary>用户动过滑块但还没点「应用」——这时不要被定时刷新覆盖。</summary>
        public bool Dirty;

        /// <summary>最近若干个转速采样，用来画趋势线。2 秒一个点，60 点 = 2 分钟。</summary>
        public readonly List<float> History = new();
    }

    // 卡片宽度：1240 窗口减去侧栏与内边距后约 950，288 宽正好排三列。
    private const double TileWidth = 288;
    private const int HistoryLength = 60;

    private static string KeyOf(FanChannel fan) => fan.Hardware + "|" + fan.Name;

    private bool TilesMatch(List<FanChannel> fans)
    {
        if (_tiles.Count != fans.Count) return false;
        foreach (var fan in fans)
            if (!_tiles.ContainsKey(KeyOf(fan))) return false;
        return true;
    }

    private void BuildTiles(List<FanChannel> fans)
    {
        // 重建时把旧的转速历史接过来，否则每次刷新曲线都会断掉
        var old = new Dictionary<string, List<float>>(StringComparer.Ordinal);
        foreach (var pair in _tiles)
            old[pair.Key] = pair.Value.History;

        FanPanel.Children.Clear();
        _tiles.Clear();

        foreach (var fan in fans)
        {
            var tile = BuildTile(fan);
            if (old.TryGetValue(KeyOf(fan), out var history))
                tile.History.AddRange(history);

            _tiles[KeyOf(fan)] = tile;
            FanPanel.Children.Add(tile.Card);
        }

        EmptyCard.Visibility = fans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (fans.Count == 0)
        {
            EmptyText.Text = !FanController.DriverPresent
                ? "一个风扇都读不到。主板那边需要 PawnIO 驱动，点上面的「安装 PawnIO」一键装上，然后点「重新读取」。"
                : "驱动已装，但这块主板的 SuperIO 芯片没能识别出来，只能等后续版本支持。";
        }
    }

    /// <summary>
    /// 单个风扇卡片。布局参考 FanControl 的 Controls 卡片：名称与实时转速同行，
    /// 下面是趋势线、滑块与操作按钮，所有信息压在一张 288×150 的卡里。
    /// </summary>
    private Tile BuildTile(FanChannel fan)
    {
        var tile = new Tile { Channel = fan };
        var root = new StackPanel();

        // ---- 第一行：名称 + 转速数值（数字用等宽字形，刷新时不会左右跳）
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = UiTheme.BodyStrong(fan.Name);
        name.FontSize = 13;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Bottom;
        name.ToolTip = $"{fan.Name}\n{fan.Hardware}"
                     + (fan.Controllable ? $"\n可写入范围 {fan.MinPercent:0}–{fan.MaxPercent:0}%" : "\n只读通道");
        top.Children.Add(name);

        tile.Rpm = new TextBlock
        {
            Text = "—",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(
            tile.Rpm, System.Windows.FontNumeralAlignment.Tabular);
        Grid.SetColumn(tile.Rpm, 1);
        top.Children.Add(tile.Rpm);

        var unit = UiTheme.Caption("RPM");
        unit.FontSize = 10;
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(3, 0, 0, 3);
        Grid.SetColumn(unit, 2);
        top.Children.Add(unit);
        tile.Unit = unit;
        root.Children.Add(top);

        // ---- 第二行：芯片型号 + 是否可调速
        var meta = new Grid { Margin = new Thickness(0, 1, 0, 0) };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hardware = UiTheme.Caption(fan.Hardware);
        hardware.FontSize = 11;
        hardware.TextTrimming = TextTrimming.CharacterEllipsis;
        meta.Children.Add(hardware);

        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 2),
            Background = UiTheme.Brush("SubtleFillColorSecondaryBrush", Brushes.Transparent),
            Child = new TextBlock
            {
                Text = fan.Controllable ? "可调速" : "仅监测",
                FontSize = 10,
                Foreground = fan.Controllable ? UiTheme.Accent : UiTheme.TextSecondary,
            },
        };
        Grid.SetColumn(badge, 1);
        meta.Children.Add(badge);
        root.Children.Add(meta);

        // ---- 第三行：最近两分钟的转速趋势；没有转速时这里放一行状态说明，
        //      否则空通道的卡片上半截就是一块白，看着像没加载出来。
        var sparkHost = new Grid
        {
            Width = TileWidth - 28,
            Height = 22,
            Margin = new Thickness(0, 8, 0, 0),
            ClipToBounds = true,
        };

        var spark = new Canvas
        {
            Width = TileWidth - 28,
            Height = 22,
            Background = Brushes.Transparent,
        };
        sparkHost.Children.Add(spark);
        tile.Spark = spark;

        tile.SparkNote = new TextBlock
        {
            FontSize = 11,
            Foreground = UiTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed,
        };
        sparkHost.Children.Add(tile.SparkNote);
        root.Children.Add(sparkHost);

        if (fan.Controllable)
        {
            // ---- 第四行：滑块 + 百分比
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                Value = Math.Clamp(fan.Percent ?? 50, 0, 100),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            slider.ValueChanged += (_, _) =>
            {
                if (tile.Suppress) return;
                tile.Dirty = true;
                if (tile.Percent is not null) tile.Percent.Text = $"{slider.Value:0}%";
                if (tile.Apply is not null) tile.Apply.IsEnabled = true;
            };
            row.Children.Add(slider);
            tile.Slider = slider;

            tile.Percent = UiTheme.Body($"{slider.Value:0}%");
            tile.Percent.FontSize = 12;
            tile.Percent.MinWidth = 34;
            tile.Percent.TextAlignment = TextAlignment.Right;
            tile.Percent.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tile.Percent, 1);
            row.Children.Add(tile.Percent);
            root.Children.Add(row);

            // ---- 第五行：自动 / 应用
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var auto = new Wpf.Ui.Controls.Button
            {
                Content = "自动",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                Padding = new Thickness(12, 3, 12, 4),
                FontSize = 12,
            };
            auto.Click += async (_, _) =>
            {
                DisableCurveByManual();
                _controller?.SetAuto(tile.Channel);
                tile.Dirty = false;
                await RefreshAsync();
            };

            var apply = new Wpf.Ui.Controls.Button
            {
                Content = "应用",
                Padding = new Thickness(12, 3, 12, 4),
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
            };
            apply.Click += async (_, _) =>
            {
                if (tile.Slider is null) return;
                DisableCurveByManual();
                var target = (float)tile.Slider.Value;
                var ok = _controller?.SetPercent(tile.Channel, target) == true;
                tile.Dirty = false;
                await RefreshAsync();
                if (ok) await VerifyWriteAsync(tile, target);
            };

            buttons.Children.Add(auto);
            buttons.Children.Add(apply);
            root.Children.Add(buttons);
            tile.Apply = apply;
        }
        else
        {
            // 只读通道：用一条细进度条代替滑块，同样能看出当前占空比
            var track = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 12, 0, 0),
                Background = UiTheme.Brush("ControlFillColorSecondaryBrush", Brushes.Transparent),
            };
            var fill = new Border
            {
                Height = 4,
                Width = 0,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = UiTheme.Accent,
                Opacity = 0.55,
            };
            var layer = new Grid();
            layer.Children.Add(track);
            layer.Children.Add(fill);
            root.Children.Add(layer);
            tile.BarFill = fill;

            var note = UiTheme.Caption("BIOS 控制，软件不可调速");
            note.FontSize = 11;
            note.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(note);
        }

        tile.Card = new Wpf.Ui.Controls.Card
        {
            Width = TileWidth,
            MinHeight = 140,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 8, 8),
            Content = root,
        };
        return tile;
    }

    /// <summary>把历史采样画成一条趋势线，右侧对齐（数据不满时从右往左长）。</summary>
    private void DrawSpark(Tile tile)
    {
        if (tile.Spark is null) return;

        var canvas = tile.Spark;
        canvas.Children.Clear();

        var values = tile.History;
        double width = canvas.Width;
        double height = canvas.Height;

        // 没有采样时画一条贴底的基线，让卡片这一块不是纯白
        if (values.Count < 2)
        {
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = width,
                Y1 = height - 1.5, Y2 = height - 1.5,
                Stroke = UiTheme.Brush("ControlStrokeColorDefaultBrush", Brushes.Gray),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
            });
            return;
        }

        // 上下各留 2 px，避免线贴着边被裁掉
        double top = 2, bottom = height - 2;

        // 窗口至少 300 RPM 高，否则风扇轻微波动会被放大成剧烈起伏
        float min = values.Min();
        float max = values.Max();
        float span = max - min;
        if (span < 300f)
        {
            var mid = (max + min) / 2;
            min = mid - 150f;
            max = mid + 150f;
            span = 300f;
        }

        var count = Math.Min(values.Count, HistoryLength);
        var start = values.Count - count;

        var points = new PointCollection(count);
        for (var i = 0; i < count; i++)
        {
            var v = Math.Clamp(values[start + i], min, max);
            var x = width - (count - 1 - i) * (width / (HistoryLength - 1));
            var y = bottom - (v - min) / span * (bottom - top);
            points.Add(new Point(x, y));
        }

        // 线下方填一层淡色，卡片不至于太空
        var area = new Polygon
        {
            Points = new PointCollection(points) { new Point(points[^1].X, height), new Point(points[0].X, height) },
            Fill = UiTheme.Accent,
            Opacity = 0.10,
        };
        canvas.Children.Add(area);

        canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = UiTheme.Accent,
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = 0.85,
        });
    }

    private void SyncTiles(List<FanChannel> fans)
    {
        foreach (var fan in fans)
        {
            if (!_tiles.TryGetValue(KeyOf(fan), out var tile)) continue;

            // 用最新快照里的通道（其中的写入句柄是稳定的）
            tile.Channel = fan;

            var rpm = fan.Rpm ?? 0;
            tile.Rpm.FontSize = rpm <= 0 ? 14 : 19;
            tile.Rpm.Text = fan.Rpm is null ? "—"
                          : fan.Rpm <= 0 ? "停转"
                          : $"{fan.Rpm:0}";
            // 「停转 RPM」这种拼接读着别扭，单位跟着数值一起藏
            tile.Unit.Visibility = rpm > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 空通道：设定值有、转速读不到。这个矛盾必须写出来，
            // 否则用户看到「67% + 停转」只会以为软件坏了。
            if (tile.SparkNote is not null)
            {
                var stopped = rpm <= 0;
                tile.SparkNote.Visibility = stopped ? Visibility.Visible : Visibility.Collapsed;
                if (stopped)
                    tile.SparkNote.Text = fan.Percent is > 0
                        ? $"设定 {fan.Percent:0}%，无转速"
                        : "无转速";
            }

            // 趋势线只在转起来的时候记，停转期间一条平线没有信息量
            if (fan.Rpm is > 0)
            {
                // 新卡片先按当前值铺满，否则刚开始只有一两个点，卡片上半截是空的
                if (tile.History.Count == 0)
                    for (var i = 0; i < HistoryLength; i++) tile.History.Add(fan.Rpm.Value);

                tile.History.Add(fan.Rpm.Value);
                if (tile.History.Count > HistoryLength * 2)
                    tile.History.RemoveRange(0, tile.History.Count - HistoryLength);
            }
            DrawSpark(tile);

            if (tile.BarFill is not null)
            {
                var pct = Math.Clamp(fan.Percent ?? 0, 0, 100);
                tile.BarFill.Width = (TileWidth - 28) * pct / 100.0;
            }

            if (tile.Slider is null || tile.Dirty || tile.Slider.IsMouseCaptureWithin)
                continue;

            var value = Math.Clamp(fan.Percent ?? tile.Slider.Value, 0, 100);
            if (Math.Abs(tile.Slider.Value - value) > 0.5)
            {
                tile.Suppress = true;
                tile.Slider.Value = value;
                tile.Suppress = false;
            }
            if (tile.Percent is not null) tile.Percent.Text = $"{tile.Slider.Value:0}%";
        }
    }

    // ------------------------------------------------------------------ 温度

    private void RenderTemps(List<TempReading> temps)
    {
        TempPanel.Children.Clear();

        var shown = 0;
        foreach (var temp in temps.Where(t => t.Value > 0).OrderByDescending(t => t.Value))
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Temperature24,
                FontSize = 16,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = HeatBrush(temp.Value),
            });

            var label = UiTheme.Body(temp.Name);
            label.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(label);

            var value = UiTheme.BodyStrong($"{temp.Value:0} °C");
            value.Margin = new Thickness(8, 0, 0, 0);
            value.VerticalAlignment = VerticalAlignment.Center;
            value.Foreground = HeatBrush(temp.Value);
            panel.Children.Add(value);

            TempPanel.Children.Add(new Border
            {
                Background = UiTheme.Brush("SubtleFillColorSecondaryBrush", Brushes.Transparent),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Child = panel,
            });
            shown++;
        }

        if (shown == 0)
            TempPanel.Children.Add(UiTheme.Caption("温度传感器不可用（通常需要 PawnIO 驱动）。"));
    }

    private static Brush HeatBrush(float celsius)
        => celsius >= 80 ? UiTheme.Critical
         : celsius >= 60 ? UiTheme.Caution
         : UiTheme.Success;

    // ------------------------------------------------------------------ 状态条

    private void UpdateStatus(List<FanChannel> fans)
    {
        // 刚弹过「调速没有生效」，别让 2 秒一次的刷新立刻盖掉
        if (DateTime.UtcNow < _verifyHold) return;

        if (_controller is null)
        {
            SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "初始化失败",
                _controller?.LastError ?? "无法打开硬件监控。", "");
            return;
        }

        var controllable = fans.Count(f => f.Controllable);

        if (!FanController.IsElevated)
        {
            SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "需要管理员权限",
                "调速要访问主板芯片，必须以管理员身份运行；当前只能查看转速。", "以管理员身份重启");
        }
        else if (!_controller.SuperIoDetected)
        {
            if (!FanController.DriverPresent)
            {
                SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "还没装驱动，读不到主板风扇",
                    "主板风扇和 CPU 温度要走内核驱动（PawnIO）。点右边的按钮一键装上，装完重开本程序就行——"
                    + "和 Fan Control 第一次运行时的做法一样。",
                    "安装 PawnIO");
            }
            else
            {
                SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, "驱动已装，但这块主板读不到风扇",
                    "PawnIO 已安装，但没能枚举出 SuperIO 芯片。可能是主板芯片型号较新、或 BIOS 里关掉了相关功能。",
                    "");
            }
        }
        else if (controllable == 0)
        {
            SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, "没有可调速的风扇",
                "主板风扇只支持监测，无法软件调速（芯片不支持或被 BIOS 锁定）。", "");
        }
        else if (fans.Where(f => f.Controllable).All(f => _ineffective.Contains(f.Name)))
        {
            SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "风扇不响应软件调速",
                "写进去的转速被驱动忽略了：能读到转速，但改不动。常见于显卡驱动的风扇策略接管，"
                + "或主板芯片不被支持。可以继续看实时数据，调速请用 BIOS 或厂商工具。", "");
        }
        else
        {
            SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Success, "风扇控制已就绪",
                $"{controllable} 个风扇可调速。", "");
        }
    }

    private void SetStatus(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message, string action)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;

        _statusAction = action;
        StatusAction.Content = action;

        if (string.IsNullOrEmpty(action))
        {
            StatusAction.Visibility = Visibility.Collapsed;
            StatusBar.Margin = new Thickness(0);
            return;
        }

        StatusAction.Visibility = Visibility.Visible;

        // 按钮是叠在 InfoBar 上的，必须给它量出宽度并从 InfoBar 里扣掉，
        // 不然长句子会绕到按钮下面（第一版就是这样，文字被压掉一半）。
        StatusAction.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var reserved = StatusAction.DesiredSize.Width + StatusAction.Margin.Right + 12;
        StatusBar.Margin = new Thickness(0, 0, reserved, 0);
    }

    private void StatusAction_Click(object sender, RoutedEventArgs e)
    {
        if (_statusAction == "以管理员身份重启")
        {
            if (RightMenuActions.Elevate())
                Application.Current.Shutdown();
            return;
        }

        if (_statusAction == "安装 PawnIO")
        {
            _ = InstallDriverAsync();
            return;
        }

        if (_statusAction == "重新读取")
        {
            _controller?.Dispose();
            _controller = null;
            _ = ReloadAsync();
        }
    }

    /// <summary>
    /// 启动 PawnIO 安装器 → 后台轮询服务是否出现 → 出现就自动重载。
    /// 安装器有界面，必须让用户点；我们只负责别把界面卡住、装完自己刷新。
    /// </summary>
    private async Task InstallDriverAsync()
    {
        var (ok, message) = FanController.StartInstaller();
        if (!ok)
        {
            SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "装不上", message, "");
            return;
        }

        SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, "正在安装驱动…",
            "安装窗口已经打开，按提示点完就行 —— 装好这里会自动刷新，不用关程序。", "");

        for (var i = 0; i < 240; i++)
        {
            await Task.Delay(1000);
            if (!FanController.DriverPresent) continue;

            SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Success, "驱动已装好",
                "正在重新读取主板风扇…", "");

            _controller?.Dispose();
            _controller = null;
            await ReloadAsync();
            return;
        }

        SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "还没检测到驱动",
            "如果安装窗口还开着，装完再点右边按钮；如果已经关了，可能被安全软件拦下了。",
            "重新读取");
    }

    // ------------------------------------------------------------------ 预设

    private async void PresetQuiet_Click(object sender, RoutedEventArgs e) => await ApplyPresetAsync(30);
    private async void PresetBalanced_Click(object sender, RoutedEventArgs e) => await ApplyPresetAsync(55);
    private async void PresetMax_Click(object sender, RoutedEventArgs e) => await ApplyPresetAsync(100);

    private async Task ApplyPresetAsync(int percent)
    {
        if (_controller is null) return;

        DisableCurveByManual();

        await Task.Run(() =>
        {
            var (fans, _) = _controller.Snapshot();
            foreach (var fan in fans.Where(f => f.Controllable))
                _controller.SetPercent(fan, percent);
        });

        _lastApplied = -1;
        ClearDirty();
        await RefreshAsync();
    }

    private async void PresetAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;

        DisableCurveByManual();

        await Task.Run(() =>
        {
            var (fans, _) = _controller.Snapshot();
            foreach (var fan in fans.Where(f => f.Controllable))
                _controller.SetAuto(fan);
        });

        _lastApplied = -1;
        ClearDirty();
        await RefreshAsync();
    }

    /// <summary>
    /// 写完等一会儿再读一次，确认真的变了。
    /// 有些显卡（比如 RX 6700 XT 走 ADL）会「接受」写入但驱动层直接忽略，
    /// 不校验的话用户点了应用看到没反应，只会以为程序坏了。
    /// </summary>
    private async Task VerifyWriteAsync(Tile tile, float target)
    {
        if (_controller is null) return;

        var name = tile.Channel.Name;
        await Task.Delay(1600);
        if (_controller is null) return;

        var (fans, _) = await Task.Run(() => _controller.Snapshot());
        var actual = fans.FirstOrDefault(f => f.Name == name)?.Percent;
        if (actual is null) return;

        if (Math.Abs(actual.Value - target) <= 12)
        {
            _ineffective.Remove(name);
            return;
        }

        _ineffective.Add(name);
        _verifyHold = DateTime.UtcNow.AddSeconds(10);

        // 显卡的话，把「为什么」说清楚：AMD 新驱动确实关掉了软件调速接口
        var isGpu = name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                 || tile.Channel.Hardware.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                 || tile.Channel.Hardware.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

        var detail = isGpu
            ? AmdFan.DescribeSupport()
            : "通常是驱动或芯片不支持软件调速（也可能被 BIOS 锁定了风扇策略）。";

        SetStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "调速没有生效",
            $"「{name}」设成 {target:0}%，实际仍是 {actual.Value:0}%。{detail}",
            "");
    }

    private void ClearDirty()
    {
        foreach (var tile in _tiles.Values)
            tile.Dirty = false;
    }

    // ------------------------------------------------------------------ 风扇曲线

    /// <summary>折线映射：≤起转温度 → 最低转速；≥满载温度 → 100%；中间线性过渡。</summary>
    private static float CurvePercent(int start, int full, int min, float temp)
    {
        if (temp <= start) return min;
        if (temp >= full) return 100;

        var span = Math.Max(1, full - start);
        var ratio = (temp - start) / (float)span;
        return min + (100 - min) * ratio;
    }

    private void LoadCurveSettings()
    {
        var cfg = AppConfig.Current;
        _curveSync = true;
        StartSlider.Value = cfg.FanCurveStart;
        FullSlider.Value = cfg.FanCurveFull;
        MinSlider.Value = cfg.FanCurveMin;
        CurveToggle.IsChecked = cfg.FanCurve;
        _curveSync = false;

        _curveEnabled = cfg.FanCurve;
        UpdateCurveLabels();
    }

    /// <summary>收起 / 展开曲线编辑区。风扇多的时候收起来，一屏就能看全所有卡片。</summary>
    private void CurveFold_Click(object sender, RoutedEventArgs e)
    {
        var collapse = CurveBody.Visibility == Visibility.Visible;
        CurveBody.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        CurveFoldIcon.Symbol = collapse
            ? Wpf.Ui.Controls.SymbolRegular.ChevronDown24
            : Wpf.Ui.Controls.SymbolRegular.ChevronUp24;
        CurveFold.ToolTip = collapse ? "展开曲线" : "收起曲线";
    }

    private void CurveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        _curveEnabled = CurveToggle.IsChecked == true;
        _lastApplied = -1;   // 重新开曲线要立刻写一次

        if (_curveEnabled)
        {
            foreach (var tile in _tiles.Values)
                tile.Dirty = false;
        }

        UpdateCurveLabels();
        QueueSave();
        _ = RefreshAsync();
    }

    private void CurveValue_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _curveSync) return;

        // 保证 起转温度 至少比 满载温度 低 5 ℃，否则曲线会退化成一条竖线
        _curveSync = true;
        if (FullSlider.Value <= StartSlider.Value + 5)
        {
            if (ReferenceEquals(sender, FullSlider))
                StartSlider.Value = Math.Max(30, FullSlider.Value - 5);
            else
                FullSlider.Value = Math.Min(95, StartSlider.Value + 5);
        }
        _curveSync = false;

        UpdateCurveLabels();
        RedrawCurve();
        QueueSave();
    }

    private void UpdateCurveLabels()
    {
        StartLabel.Text = $"起转温度 {StartSlider.Value:0} °C";
        FullLabel.Text = $"满载温度 {FullSlider.Value:0} °C";
        MinLabel.Text = $"最低转速 {MinSlider.Value:0} %";

        CurveSummary.Text = _curveEnabled
            ? $"已启用：{StartSlider.Value:0} °C 以下 {MinSlider.Value:0}% · "
              + $"{FullSlider.Value:0} °C 以上 100% · 中间线性过渡"
            : "按最高温度自动调节转速：低温静音，高温全速。启用后手动滑块会被接管。";
    }

    private void QueueSave()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick -= SaveTick;
        _saveTimer.Tick += SaveTick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        AppConfig.Remember(cfg =>
        {
            cfg.FanCurve = _curveEnabled;
            cfg.FanCurveStart = (int)StartSlider.Value;
            cfg.FanCurveFull = (int)FullSlider.Value;
            cfg.FanCurveMin = (int)MinSlider.Value;
        });
    }

    private void CurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawCurve();

    /// <summary>把曲线画出来：网格 + 面积 + 折线 + 当前温度游标。</summary>
    private void RedrawCurve()
    {
        if (!_ready || CurveCanvas is null) return;

        var width = CurveCanvas.ActualWidth;
        var height = CurveCanvas.ActualHeight;
        if (width < 60 || height < 60) return;

        CurveCanvas.Children.Clear();

        const double padLeft = 34, padRight = 14, padTop = 12, padBottom = 22;
        var plotW = width - padLeft - padRight;
        var plotH = height - padTop - padBottom;
        if (plotW <= 10 || plotH <= 10) return;

        // X：30–95 ℃；Y：0–100 %
        double X(double temp) => padLeft + (Math.Clamp(temp, 30, 95) - 30) / 65.0 * plotW;
        double Y(double percent) => padTop + (1 - Math.Clamp(percent, 0, 100) / 100.0) * plotH;

        var gridBrush = UiTheme.Brush("ControlStrokeColorDefaultBrush", Brushes.Gray);
        var accent = UiTheme.Accent;

        // 横向网格线 + 左侧百分比刻度
        foreach (var percent in new double[] { 0, 25, 50, 75, 100 })
        {
            CurveCanvas.Children.Add(new Line
            {
                X1 = padLeft, X2 = padLeft + plotW,
                Y1 = Y(percent), Y2 = Y(percent),
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = percent is 0 or 100 ? 0.85 : 0.4,
            });

            var label = new TextBlock
            {
                Text = $"{percent:0}",
                FontSize = 10,
                Foreground = UiTheme.TextSecondary,
            };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, Y(percent) - 8);
            CurveCanvas.Children.Add(label);
        }

        // 底部温度刻度
        foreach (var temp in new double[] { 30, 45, 60, 75, 90 })
        {
            var label = new TextBlock
            {
                Text = $"{temp:0}°",
                FontSize = 10,
                Foreground = UiTheme.TextSecondary,
            };
            Canvas.SetLeft(label, X(temp) - 9);
            Canvas.SetTop(label, padTop + plotH + 4);
            CurveCanvas.Children.Add(label);
        }

        var start = StartSlider.Value;
        var full = FullSlider.Value;
        var min = MinSlider.Value;

        var line = new PointCollection
        {
            new(X(30), Y(min)),
            new(X(start), Y(min)),
            new(X(full), Y(100)),
            new(X(95), Y(100)),
        };

        // 曲线下方的淡色面积
        var area = new PointCollection(line) { new(X(95), Y(0)), new(X(30), Y(0)) };
        CurveCanvas.Children.Add(new Polygon
        {
            Points = area,
            Fill = accent,
            Opacity = _curveEnabled ? 0.16 : 0.07,
        });

        CurveCanvas.Children.Add(new Polyline
        {
            Points = line,
            Stroke = accent,
            StrokeThickness = 2.4,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = _curveEnabled ? 1 : 0.5,
        });

        // 两个拐点
        foreach (var point in new[] { new Point(X(start), Y(min)), new Point(X(full), Y(100)) })
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = accent,
                Opacity = _curveEnabled ? 1 : 0.5,
            };
            Canvas.SetLeft(dot, point.X - 4);
            Canvas.SetTop(dot, point.Y - 4);
            CurveCanvas.Children.Add(dot);
        }

        // 当前温度游标
        if (_maxTemp > 0)
        {
            var cursorX = X(_maxTemp);
            CurveCanvas.Children.Add(new Line
            {
                X1 = cursorX, X2 = cursorX,
                Y1 = padTop, Y2 = padTop + plotH,
                Stroke = HeatBrush(_maxTemp),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });

            var percent = CurvePercent((int)start, (int)full, (int)min, _maxTemp);
            var marker = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = HeatBrush(_maxTemp),
                Stroke = UiTheme.Brush("SolidBackgroundFillColorBaseBrush", Brushes.White),
                StrokeThickness = 2,
            };
            Canvas.SetLeft(marker, cursorX - 5);
            Canvas.SetTop(marker, Y(percent) - 5);
            CurveCanvas.Children.Add(marker);

            var caption = new TextBlock
            {
                Text = $"{_maxTemp:0}°C · {percent:0}%",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = HeatBrush(_maxTemp),
            };
            Canvas.SetLeft(caption, Math.Min(cursorX + 8, width - 72));
            Canvas.SetTop(caption, Math.Max(padTop, Y(percent) - 20));
            CurveCanvas.Children.Add(caption);
        }
    }

    /// <summary>手动操作意味着用户要接管，曲线自动关掉，避免两边打架。</summary>
    private void DisableCurveByManual()
    {
        if (!_curveEnabled) return;

        _curveEnabled = false;
        _ready = false;
        CurveToggle.IsChecked = false;
        _ready = true;
        UpdateCurveLabels();
        QueueSave();
    }
}
