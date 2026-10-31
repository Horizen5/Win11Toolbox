using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Win11Toolbox.Controls;
using Microsoft.Win32;
using Win11Toolbox.Services;

namespace Win11Toolbox.Pages;

public partial class UtilitiesPage : Page
{
    // 2FA：账户只在这个集合里，页面实例被回收时一起没。不写盘、不缓存、不序列化。
    private readonly ObservableCollection<TotpAccount> _totp = new();
    private DispatcherTimer? _totpTimer;

    public UtilitiesPage()
    {
        InitializeComponent();
        LocalIpText.Text = $"内网 IP：{ShellTools.LocalIp()}";

        TotpList.ItemsSource = _totp;
        Loaded += (_, _) => StartTotpTimer();
        Unloaded += (_, _) => StopTotpTimer();
    }

    // ------------------------------------------------------------------ 哈希
    private void PickFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要校验的文件" };
        if (dialog.ShowDialog() == true)
            HashPath.Text = dialog.FileName;
    }

    private async void Hash_Click(object sender, RoutedEventArgs e)
    {
        var path = HashPath.Text;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            HashResult.Text = "请先选择文件";
            return;
        }

        var algorithm = (HashAlgorithm.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SHA-256";
        HashResult.Text = "计算中…";
        try
        {
            HashResult.Text = await Task.Run(() => ShellTools.HashFile(path, algorithm));
        }
        catch (Exception ex)
        {
            HashResult.Text = $"计算失败：{ex.Message}";
        }
    }

    // ------------------------------------------------------------------ 编码
    private void Base64Encode_Click(object sender, RoutedEventArgs e)
        => CodecOutput.Text = ShellTools.Base64Encode(CodecInput.Text);

    private void Base64Decode_Click(object sender, RoutedEventArgs e)
        => CodecOutput.Text = ShellTools.Base64Decode(CodecInput.Text);

    private void UrlEncode_Click(object sender, RoutedEventArgs e)
        => CodecOutput.Text = ShellTools.UrlEncode(CodecInput.Text);

    private void UrlDecode_Click(object sender, RoutedEventArgs e)
        => CodecOutput.Text = ShellTools.UrlDecode(CodecInput.Text);

    // ------------------------------------------------------------------ 密码
    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var length = (int)Math.Clamp(PasswordLength.Value ?? 16, 6, 64);
        var password = ShellTools.GeneratePassword(length, true, true, true, true);
        PasswordResult.Text = password;
        var (_, label) = ShellTools.PasswordStrength(password);
        PasswordStrength.Text = $"强度 {label}（{password.Length} 位）";
        try
        {
            Clipboard.SetText(password);
        }
        catch
        {
            // 剪贴板被其他程序占用时忽略
        }
    }

    // ------------------------------------------------------------------ 2FA
    private void StartTotpTimer()
    {
        if (_totp.Count == 0)
            return;

        if (_totpTimer is null)
        {
            // 200ms 一跳：秒数一秒变一次，圆圈的进度却是连续的，
            // 用 1s 的定时器会让圆圈一秒一顿，看着像卡住。
            _totpTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            _totpTimer.Tick += (_, _) => TickTotp();
        }

        if (!_totpTimer.IsEnabled)
            _totpTimer.Start();

        TickTotp();
    }

    private void StopTotpTimer() => _totpTimer?.Stop();

    private void TickTotp()
    {
        if (_totp.Count == 0)
        {
            StopTotpTimer();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var account in _totp)
            account.Tick(now);
    }

    private void AddTotp_Click(object sender, RoutedEventArgs e)
    {
        if (!Totp.TryParse(TotpSecret.Text, out var account, out var error) || account is null)
        {
            ShowTotpHint(error);
            return;
        }

        // 用户填了备注就用备注；没填就用 otpauth 里的账户名 / 发行方
        account.Rename(TotpLabel.Text);

        HideTotpHint();
        TotpSecret.Text = "";
        TotpLabel.Text = "";

        account.Tick(DateTimeOffset.UtcNow);
        _totp.Add(account);

        TotpEmpty.Visibility = Visibility.Collapsed;
        TotpClearAll.Visibility = Visibility.Visible;
        StartTotpTimer();

        // 连续加几个账户时不用再点一下输入框
        TotpSecret.Focus();
    }

    private void TotpSecret_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
            return;

        e.Handled = true;
        AddTotp_Click(sender, new RoutedEventArgs());
    }

    private void CopyTotp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not TotpAccount account)
            return;

        try
        {
            Clipboard.SetText(account.RawCode);
        }
        catch
        {
            // 剪贴板被别的程序独占时忽略，不弹窗打断
            return;
        }

        if (sender is Button button)
        {
            button.Content = "已复制";
            var restore = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            restore.Tick += (_, _) =>
            {
                restore.Stop();
                button.Content = "复制";
            };
            restore.Start();
        }

        ScheduleClipboardWipe(account.RawCode);
    }

    /// <summary>
    /// 45 秒后把剪贴板里的验证码抹掉 —— 只在内容还是这串数字时动手，
    /// 期间用户自己复制了别的东西就不碰，免得把人家的内容清掉。
    /// </summary>
    private static async void ScheduleClipboardWipe(string code)
    {
        await Task.Delay(TimeSpan.FromSeconds(45));

        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == code)
                Clipboard.Clear();
        }
        catch
        {
            // 剪贴板被占用就放弃，不影响使用
        }
    }

    private void RemoveTotp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not TotpAccount account)
            return;

        _totp.Remove(account);
        account.Dispose();

        if (_totp.Count == 0)
        {
            TotpEmpty.Visibility = Visibility.Visible;
            TotpClearAll.Visibility = Visibility.Collapsed;
            StopTotpTimer();
        }
    }

    private void ClearTotp_Click(object sender, RoutedEventArgs e)
    {
        foreach (var account in _totp)
            account.Dispose();

        _totp.Clear();
        TotpEmpty.Visibility = Visibility.Visible;
        TotpClearAll.Visibility = Visibility.Collapsed;
        StopTotpTimer();
    }

    private void ShowTotpHint(string message)
    {
        TotpHint.Text = message;
        TotpHint.Visibility = Visibility.Visible;
    }

    private void HideTotpHint()
    {
        TotpHint.Text = "";
        TotpHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 诊断专用（<c>--navshot utilities --uitab</c>）：切到 2FA 页并塞两个演示账户，
    /// 好把倒计时圆圈真的截下来看。用的都是公开测试密钥，不是谁的账号。
    /// </summary>
    internal void ShowTotpDemo()
    {
        Tabs.SelectedIndex = 3;

        if (_totp.Count > 0)
            return;

        TotpLabel.Text = "GitHub · alice";
        TotpSecret.Text = "JBSWY3DPEHPK3PXP";
        AddTotp_Click(this, new RoutedEventArgs());

        TotpLabel.Text = "Microsoft · bob";
        TotpSecret.Text = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        AddTotp_Click(this, new RoutedEventArgs());
    }

    // ------------------------------------------------------------------ 网络
    private async void CheckPort_Click(object sender, RoutedEventArgs e)
    {
        var port = (int)Math.Clamp(PortNumber.Value ?? 443, 1, 65535);
        PortResult.Text = "检测中…";
        var (ok, message) = await ShellTools.CheckPortAsync(PortHost.Text.Trim(), port);
        PortResult.Text = (ok ? "✓ " : "✗ ") + message;
        PortResult.Foreground = ok ? UiTheme.Success : UiTheme.Critical;
    }

    private async void Ping_Click(object sender, RoutedEventArgs e)
    {
        var host = PingHost.Text.Trim();
        if (host.Length == 0) return;

        PingOutput.Text = $"正在 Ping {host}…";
        PingOutput.Text = await ShellTools.PingAsync(host, 4);
    }
}
