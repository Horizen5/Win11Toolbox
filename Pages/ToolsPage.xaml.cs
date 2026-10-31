using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Wpf.Ui.Controls;
using Win11Toolbox.Controls;
using Win11Toolbox.Services;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace Win11Toolbox.Pages;

public partial class ToolsPage : Page, IRefreshablePage
{
    private const int Columns = 4;

    private sealed class CategoryUi
    {
        public TextBlock Header = null!;
        public Card Card = null!;
        public List<(ToolItem Item, ToolTile Tile)> Tiles = new();
    }

    private readonly List<CategoryUi> _categories = new();

    public ToolsPage()
    {
        InitializeComponent();
        Subtitle.Text = $"集成了 {ToolCatalog.TotalCount} 个 Windows 常用工具，点击磁贴即可启动";
        BuildAll();
        ApplyFilter(string.Empty);
    }

    /// <summary>磁贴只创建一次，搜索时只切换可见性，避免每敲一个字就重建几十个控件。</summary>
    private void BuildAll()
    {
        foreach (var category in ToolCatalog.Categories)
        {
            var header = UiTheme.BodyStrong(category.Title);
            header.Margin = new Thickness(2, 6, 0, 8);
            Sections.Children.Add(header);

            var card = new Card { Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 10) };
            var grid = new UniformGrid { Columns = Columns };

            var ui = new CategoryUi { Header = header, Card = card };

            foreach (var item in category.Items)
            {
                var tile = new ToolTile(item.Name, item.Description, AppCache.Icon(item), ToolCatalog.Glyph(item))
                {
                    Margin = new Thickness(2),
                };
                tile.Clicked += () => Launch(item);
                grid.Children.Add(tile);
                ui.Tiles.Add((item, tile));
            }

            card.Content = grid;
            Sections.Children.Add(card);
            _categories.Add(ui);
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    // ------------------------------------------------------------------ F5 / Ctrl+F
    public void RefreshPage()
    {
        // 工具目录是编译期固定的，没有可刷新的数据；F5 只把筛选条件清掉
        SearchBox.Text = string.Empty;
        ApplyFilter(string.Empty);
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void ApplyFilter(string keyword)
    {
        var query = keyword.Trim();
        var total = 0;

        foreach (var category in _categories)
        {
            var visible = 0;
            foreach (var (item, tile) in category.Tiles)
            {
                var match = query.Length == 0
                            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase);
                tile.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match) visible++;
            }

            total += visible;
            var show = visible > 0;
            category.Header.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            category.Card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountHint.Text = query.Length == 0 ? $"共 {total} 个工具" : $"找到 {total} 个工具";
    }

    private void Launch(ToolItem item)
    {
        try
        {
            // Launch 返回非 null 表示「没能按原样启动，但有话要说」（如 Windows 终端没装 → 已退回命令提示符）
            var notice = ToolCatalog.Launch(item);
            if (notice is not null)
                ShowMessage("提示", notice);
        }
        catch (Exception ex)
        {
            ShowMessage("启动失败", $"{item.Name}：{ex.Message}");
        }
    }

    private static void ShowMessage(string title, string message)
    {
        try
        {
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                CloseButtonText = "好的",
                ShowTitle = true,
            };
            _ = dialog.ShowDialogAsync();
        }
        catch
        {
            // 忽略
        }
    }
}
