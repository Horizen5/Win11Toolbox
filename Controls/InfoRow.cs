using System.Windows;
using System.Windows.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using Wpf.Ui.Controls;

namespace Win11Toolbox.Controls;

/// <summary>一行「标签 + 值」，行高与 Windows 设置里的信息行一致（约 40px）。</summary>
public sealed class InfoRow : Grid
{
    public InfoRow(string key, string value, double keyWidth = 130, bool divider = false)
    {
        Margin = new Thickness(0);

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (divider)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top };
            line.SetResourceReference(Border.BackgroundProperty, "CardStrokeColorDefaultBrush");
            SetRow(line, 0);
            SetColumnSpan(line, 2);
            Children.Add(line);
        }
        else
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var row = divider ? 1 : 0;

        var label = new TextBlock
        {
            Text = key,
            FontTypography = FontTypography.Body,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        SetRow(label, row);
        SetColumn(label, 0);
        Children.Add(label);

        var content = new TextBlock
        {
            Text = value,
            FontTypography = FontTypography.Body,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetRow(content, row);
        SetColumn(content, 1);
        Children.Add(content);
    }
}
