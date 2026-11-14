using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Win11Toolbox.Controls;
using Win11Toolbox.Services;
using Win11Toolbox.Services.Localization;
using Wpf.Ui.Appearance;

namespace Win11Toolbox.Pages;

public partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        VersionText.Text = Localizer.T("Win11 工具箱") + " " + AppInfo.Version;

        // 按配置回填开关，回填过程中不要触发保存
        var theme = AppConfig.Current.Theme;
        FollowSwitch.IsChecked = theme == "auto";
        DarkSwitch.IsChecked = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        DarkSwitch.IsEnabled = theme != "auto";

        // 语言下拉框：第一项是「自动（跟随系统）」，其余十种按各自语言显示名称
        LangCombo.ItemsSource = Localizer.Languages;
        LangCombo.SelectedValue = Localizer.ConfiguredCode;
        Localizer.LanguageChanged += RefreshCoverage;
        RefreshCoverage();

        _loading = false;
    }

    private void RefreshCoverage()
    {
        CoverageText.Text =
            Localizer.T("当前语言已翻译") + " " + Localizer.TranslatedCount + " " +
            Localizer.T("项界面文案") + " · " + Localizer.T("未翻译的条目保持简体中文");
    }

    private void Lang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LangCombo.SelectedValue is not string code)
            return;

        Localizer.SetLanguage(code);
        RefreshCoverage();
    }

    private void Dark_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || FollowSwitch.IsChecked == true)
            return;

        bool dark = DarkSwitch.IsChecked == true;

        Controls.ThemeSwitch.Run(() =>
        {
            ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
            AppConfig.Remember(c => c.Theme = dark ? "dark" : "light");
            Controls.UiTheme.NotifyThemeChanged();
        });
    }

    private void Follow_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (FollowSwitch.IsChecked == true)
        {
            Controls.ThemeSwitch.Run(() =>
            {
                ApplicationThemeManager.ApplySystemTheme();
                AppConfig.Remember(c => c.Theme = "auto");
                Controls.UiTheme.NotifyThemeChanged();
            });

            DarkSwitch.IsEnabled = false;
            DarkSwitch.IsChecked = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        }
        else
        {
            DarkSwitch.IsEnabled = true;
            AppConfig.Remember(c => c.Theme = DarkSwitch.IsChecked == true ? "dark" : "light");
        }
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win11Toolbox");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }
}
