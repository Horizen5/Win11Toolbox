using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using Wpf.Ui.Controls;

namespace Win11Toolbox.Controls;

/// <summary>
/// 首页的四宫格监控卡：图标胶囊 + 标题 + 大号数值 + 每秒一根的柱状历史 + 脚注。
///
/// 直接继承 Border 而不是 WPF-UI 的 Card：Card 默认背景是近乎透明的白，
/// 四张卡并排时会连成一整条，看不出边界。这里自己给背景、1px 描边和圆角，
/// 每张卡才有明确的"块"感。
/// </summary>
public sealed class MetricCard : Border
{
    private readonly TextBlock _value;
    private readonly TextBlock _unit;
    private readonly TextBlock _foot;
    private readonly MetricBars _bars;

    public MetricCard(SymbolRegular icon, string title, string unit,
                      bool percent = true, double floor = 20, double ceiling = 0)
    {
        Padding = new Thickness(14, 12, 14, 12);
        MinHeight = 158;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var panel = new StackPanel();

        // 图标放在一枚淡色胶囊里：既是点缀，也让四张卡各有各的"脸"
        var chip = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chip.SetResourceReference(BackgroundProperty, "SystemFillColorAttentionBackgroundBrush");

        var iconElement = new SymbolIcon
        {
            Symbol = icon,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconElement.SetResourceReference(SymbolIcon.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        chip.Child = iconElement;

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(chip);

        var titleBlock = UiTheme.Caption(title);
        titleBlock.Margin = new Thickness(8, 0, 0, 0);
        titleBlock.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(titleBlock);
        panel.Children.Add(header);

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _value = new TextBlock { Text = "—", FontTypography = FontTypography.Title, FontSize = 26 };
        _unit = UiTheme.Caption(unit);
        _unit.Margin = new Thickness(4, 0, 0, 5);
        _unit.VerticalAlignment = VerticalAlignment.Bottom;
        valueRow.Children.Add(_value);
        valueRow.Children.Add(_unit);
        panel.Children.Add(valueRow);

        _bars = new MetricBars
        {
            IsPercent = percent,
            Floor = floor,
            FixedCeiling = ceiling,
            Capacity = 30,
            Height = 52,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _bars.SetResourceReference(MetricBars.BarBrushProperty, "AccentFillColorDefaultBrush");
        _bars.SetResourceReference(MetricBars.CautionBrushProperty, "SystemFillColorCautionBrush");
        _bars.SetResourceReference(MetricBars.CriticalBrushProperty, "SystemFillColorCriticalBrush");
        _bars.SetResourceReference(MetricBars.BaselineBrushProperty, "DividerStrokeColorDefaultBrush");
        panel.Children.Add(_bars);

        _foot = UiTheme.Caption(string.Empty);
        _foot.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(_foot);

        Child = panel;
    }

    public void Update(string value, string? foot = null, double? sparkValue = null)
    {
        _value.Text = value;
        if (foot is not null)
            _foot.Text = foot;
        if (sparkValue.HasValue)
            _bars.Push(sparkValue.Value);
    }
}
