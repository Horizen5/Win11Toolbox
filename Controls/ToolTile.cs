using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;

namespace Win11Toolbox.Controls;

/// <summary>可点击的工具磁贴：图标 + 名称 + 说明，悬停时出现描边。</summary>
public sealed class ToolTile : Border
{
    public event Action? Clicked;
    private readonly Border _iconHost;
    private bool _hover;

    public ToolTile(string title, string description, ImageSource? icon = null, SymbolRegular? symbol = null)
    {
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(0);
        Padding = new Thickness(10);
        Margin = new Thickness(0);
        Background = new SolidColorBrush(Colors.Transparent);
        Cursor = Cursors.Hand;
        MinHeight = 72;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _iconHost = new Border { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        if (symbol is not null)
        {
            // 字形优先：和系统「设置」应用保持同一套语汇，也不会出现取不到图标就空白的情况。
            // 字号 24 是 32px 图标格里最贴近设置应用的比例。
            _iconHost.Child = new SymbolIcon
            {
                Symbol = symbol.Value,
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else if (icon is not null)
            _iconHost.Child = new Image { Source = icon, Width = 28, Height = 28 };
        grid.Children.Add(_iconHost);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var titleBlock = UiTheme.Body(title);
        titleBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        texts.Children.Add(titleBlock);
        var descBlock = UiTheme.Caption(description);
        descBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        descBlock.Margin = new Thickness(0, 1, 0, 0);
        texts.Children.Add(descBlock);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        Child = grid;

        MouseEnter += (_, _) =>
        {
            _hover = true;
            Background = UiTheme.Brush("ControlFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(12, 0, 0, 0)));
        };
        MouseLeave += (_, _) =>
        {
            _hover = false;
            Background = new SolidColorBrush(Colors.Transparent);
        };
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
    }

    public bool Hovered => _hover;
}
