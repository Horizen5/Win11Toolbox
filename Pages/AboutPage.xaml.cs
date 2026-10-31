using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Win11Toolbox.Pages;

public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        VersionLine.Text = "Win11 工具箱 " + Services.AppInfo.Version;
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    private void OpenTuba_Click(object sender, RoutedEventArgs e)
        => Open("https://github.com/luolangaga/tubatools");

    private void OpenSys_Click(object sender, RoutedEventArgs e)
        => Open("https://github.com/Hikarisame-Technology/SYSTools");

    private void OpenWpfUi_Click(object sender, RoutedEventArgs e)
        => Open("https://github.com/lepoco/wpfui");
}
