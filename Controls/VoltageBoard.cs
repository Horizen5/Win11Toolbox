using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Win11Toolbox.Services;
using Win11Toolbox.Services.Localization;

namespace Win11Toolbox.Controls;

/// <summary>
/// 电源电压面板：+12V / +5V / +3.3V 三路主轨道 + 可折叠的杂项电压。
///
/// <para>
/// 纯展示控件，数据由 <see cref="VoltageMonitor"/> 提供 —— 采集在 FanController 那边，
/// 这里只负责画。硬件信息页和风扇页都可以直接放一个。
/// </para>
/// <para>
/// 卡片只建一次，之后只改文本：定时刷新每秒都可能来一轮，重建控件会闪、也会丢滚动位置。
/// </para>
/// </summary>
public sealed class VoltageBoard : StackPanel
{
    private static readonly (VoltageRail Rail, string Label, float Nominal)[] Rails =
    {
        (VoltageRail.V12, "+12V", 12f),
        (VoltageRail.V5, "+5V", 5f),
        (VoltageRail.V33, "+3.3V", 3.3f),
    };

    private sealed class RailCard
    {
        public required Border Card { get; init; }
        public required Wpf.Ui.Controls.TextBlock Value { get; init; }
        public required Wpf.Ui.Controls.TextBlock Detail { get; init; }
        public required Wpf.Ui.Controls.TextBlock State { get; init; }
    }

    private readonly WrapPanel _railPanel = new();
    private readonly Wpf.Ui.Controls.TextBlock _summary;
    private readonly Grid _otherHead;
    private readonly Wpf.Ui.Controls.TextBlock _otherHeader;
    private readonly Wpf.Ui.Controls.SymbolIcon _otherChevron;
    private readonly WrapPanel _otherPanel = new();
    private readonly Dictionary<VoltageRail, RailCard> _cards = new();
    private readonly Dictionary<string, Wpf.Ui.Controls.TextBlock> _otherValues = new(StringComparer.Ordinal);

    private bool _expanded;
    private string _otherSignature = "\u0001";
    private string _summaryText = "";

    private static string L(string text) => Localizer.T(text);

    public VoltageBoard()
    {
        BuildRailCards();

        Children.Add(_railPanel);

        _summary = UiTheme.Caption(L("等待读数…"));
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(_summary);

        _otherHeader = UiTheme.BodyStrong(L("其他电压"));
        _otherHeader.VerticalAlignment = VerticalAlignment.Center;

        _otherChevron = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiTheme.TextSecondary,
        };

        _otherHead = new Grid
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = L("Vcore / VID / AVCC / 待机电压等非 ATX 输出轨"),
        };
        var headRow = new StackPanel { Orientation = Orientation.Horizontal };
        headRow.Children.Add(new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.Info24,
            FontSize = 15,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiTheme.TextSecondary,
        });
        headRow.Children.Add(_otherHeader);
        _otherHead.Children.Add(headRow);
        _otherHead.Children.Add(_otherChevron);
        _otherHead.MouseLeftButtonUp += (_, _) => ToggleOthers();
        Children.Add(_otherHead);

        _otherPanel.Visibility = Visibility.Collapsed;
        _otherPanel.Margin = new Thickness(0, 8, 0, 0);
        Children.Add(_otherPanel);
    }

    /// <summary>用最新一帧数据刷新显示。可以高频调用。</summary>
    public void Update(VoltageMonitor monitor)
    {
        SyncRails(monitor);
        SyncOthers(monitor);

        _summaryText = monitor.Count == 0
            ? L("没有读到电压传感器，通常需要管理员权限和 PawnIO 驱动。")
            : L("读数取自主板 Super I/O 的 ADC（与 BIOS 同源）；+12V / +5V 按板型分压系数还原，12V 轨分辨率约 0.096 V。绝对精度受 ADC 量化限制，压降变化比绝对值更可信。");
        if (_summary.Text != _summaryText)
            _summary.Text = _summaryText;
    }

    private void BuildRailCards()
    {
        _railPanel.Children.Clear();
        _cards.Clear();

        foreach (var (rail, label, nominal) in Rails)
        {
            var value = UiTheme.Title("—");
            value.FontSize = 26;
            value.Margin = new Thickness(0, 4, 0, 0);

            var detail = UiTheme.Caption(
                $"{L("标称")} {nominal:0.00} V · {L("允许")} {nominal * 0.95f:0.00}–{nominal * 1.05f:0.00} V");
            detail.Margin = new Thickness(0, 2, 0, 0);

            var state = UiTheme.Caption(L("等待读数…"));
            state.Margin = new Thickness(0, 4, 0, 0);
            state.TextWrapping = TextWrapping.Wrap;

            var body = new StackPanel();
            body.Children.Add(UiTheme.BodyStrong(label));
            body.Children.Add(value);
            body.Children.Add(detail);
            body.Children.Add(state);

            var border = new Border
            {
                Width = 244,
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 10, 10),
                CornerRadius = new CornerRadius(8),
                Background = UiTheme.Brush("SubtleFillColorSecondaryBrush", Brushes.Transparent),
                Child = body,
            };

            _cards[rail] = new RailCard { Card = border, Value = value, Detail = detail, State = state };
            _railPanel.Children.Add(border);
        }
    }

    private void SyncRails(VoltageMonitor monitor)
    {
        foreach (var (rail, _, _) in Rails)
        {
            if (!_cards.TryGetValue(rail, out var card)) continue;

            var reading = monitor.Find(rail);
            if (reading is null || !reading.HasBaseline)
            {
                card.Value.Text = "—";
                card.Value.Foreground = UiTheme.Neutral;
                card.State.Text = L("未检测到该轨道的传感器");
                card.State.Foreground = UiTheme.Neutral;
                card.Card.ToolTip = null;
                continue;
            }

            card.Value.Text = $"{reading.Value:0.000} V";
            card.Value.Foreground = reading.InTolerance ? UiTheme.Success : UiTheme.Caution;

            var status = reading.InTolerance ? L("正常") : L("超出 ±5%");
            card.State.Text = $"{status} · {L("最低")} {reading.Min:0.000} · {L("最高")} {reading.Max:0.000} · "
                            + $"{L("压降")} {reading.DropPercent:0.0}%";
            card.State.Foreground = reading.InTolerance ? UiTheme.TextSecondary : UiTheme.Caution;

            card.Card.ToolTip = $"{L("原始")} {reading.Raw:0.000} V × {reading.Gain:0.##} = {reading.Value:0.000} V"
                              + $" · {(reading.BoardCalibrated ? L("板型系数") : L("原始值"))}\n{reading.Identifier}";
        }
    }

    private void SyncOthers(VoltageMonitor monitor)
    {
        var others = monitor.Others;
        var signature = string.Join("|", others.Select(v => v.Identifier));
        if (signature == _otherSignature)
        {
            foreach (var v in others)
            {
                if (_otherValues.TryGetValue(v.Identifier, out var text))
                    text.Text = $"{v.Value:0.000} V";
            }
            return;
        }

        _otherSignature = signature;
        _otherValues.Clear();
        _otherPanel.Children.Clear();

        foreach (var v in others.Take(30))
        {
            var value = UiTheme.BodyStrong($"{v.Value:0.000} V");
            value.FontSize = 13;
            value.VerticalAlignment = VerticalAlignment.Center;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(UiTheme.Caption(v.Name));
            row.Children.Add(value);

            _otherPanel.Children.Add(new Border
            {
                Background = UiTheme.Brush("SubtleFillColorSecondaryBrush", Brushes.Transparent),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 8),
                Child = row,
                ToolTip = v.Identifier,
            });
            _otherValues[v.Identifier] = value;
        }

        _otherHeader.Text = L("其他电压") + $" ({others.Count})";
        _otherHead.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleOthers()
    {
        if (_otherHead.Visibility != Visibility.Visible) return;
        _expanded = !_expanded;
        _otherPanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _otherChevron.Symbol = _expanded
            ? Wpf.Ui.Controls.SymbolRegular.ChevronUp24
            : Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
    }
}
