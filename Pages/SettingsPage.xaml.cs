using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Win11Toolbox.Controls;
using Win11Toolbox.Services;
using Wpf.Ui.Appearance;

namespace Win11Toolbox.Pages;

public partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();

        VersionText.Text = "Win11 工具箱 " + AppInfo.Version;

        // 按配置回填开关，回填过程中不要触发保存
        var theme = AppConfig.Current.Theme;
        FollowSwitch.IsChecked = theme == "auto";
        DarkSwitch.IsChecked = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        DarkSwitch.IsEnabled = theme != "auto";

        _loading = false;
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
