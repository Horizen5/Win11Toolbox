using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Win11Toolbox.Services;

/// <summary>装机清单里的一款软件。</summary>
public sealed record InstallApp(string Category, string Name, string Id, string Desc);

/// <summary>已安装的一个应用（Appx / 预装包）。</summary>
public sealed record InstalledApp(string Name, string PackageFullName, string Version, string Source);

/// <summary>
/// 应用管理：winget 装机清单、Appx 卸载、系统还原点。
///
/// <para>
/// 这里全部走外部进程（winget.exe / powershell.exe），因为这三件事都没有稳定的
/// 公开托管 API：winget 的 COM 接口不保证版本兼容，Appx 的部署接口只有 WinRT 版
/// 而且卸载语义和自己写一遍差不多。调命令行反而是最稳的。
/// </para>
/// <para>
/// <b>输出必须边跑边读。</b>winget 安装一个包能刷几十行进度，重定向管道默认只有
/// 4 KB 缓冲区，父进程不读就会把子进程卡死在 WriteFile 上 —— 表现是"卡在安装中"
/// 永远不动。所以下面统一用 BeginOutputReadLine/BeginErrorReadLine 异步收，
/// stdout 和 stderr 都要收。
/// </para>
/// </summary>
public static class AppManager
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    // ------------------------------------------------------------------ winget
    /// <summary>
    /// winget 是否可用。没有 App Installer 的系统上会是 false。
    ///
    /// <para>
    /// 这个必须是 async 的。以前写成 <c>RunAsync(...).GetAwaiter().GetResult()</c>，
    /// 在 UI 线程上调用时必死锁：RunAsync 里 <c>await WaitForExitAsync</c> 的续体要回
    /// 调用方所在的同步上下文（就是 UI 线程），而 UI 线程正卡在 GetResult 上等它。
    /// </para>
    /// </summary>
    public static async Task<bool> HasWingetAsync()
    {
        try
        {
            var (exit, _) = await RunAsync("winget", "--version", null, CancellationToken.None,
                TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            return exit == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一次性取出「哪些包已经装了」。
    ///
    /// <para>
    /// 一开始是每个包跑一次 <c>winget list --id X -e</c>，58 款软件就是 58 个进程，
    /// 4 路并发也要 8 秒以上，还得压着 UI 线程刷进度。改成整表跑一次
    /// <c>winget list</c>（本机约 2.3 秒），再把输出切成 token 去匹配 ID —— 快了一个数量级。
    /// </para>
    /// <para>
    /// 匹配只认「整段等于 ID」的 token：ID 里不会有空格，所以按空格切开逐个比是安全的，
    /// 也不受中英文混排导致表格对不齐的影响（winget 是按字符数补齐的，含中文的行会错位）。
    /// </para>
    /// </summary>
    public static async Task<HashSet<string>> InstalledIdsAsync(
        IEnumerable<string> candidates, CancellationToken ct)
    {
        var wanted = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (exit, output) = await RunAsync("winget",
            "list --accept-source-agreements --disable-interactivity",
            null, ct, TimeSpan.FromMinutes(3));

        if (exit != 0) return found;

        foreach (var line in output.Split('\n'))
        {
            foreach (var token in line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (wanted.Contains(token))
                    found.Add(token);
            }
        }

        return found;
    }

    /// <summary>安装一个包。<paramref name="silent"/> 走 --silent（能静默的都静默）。</summary>
    public static async Task<(bool Ok, string Output)> InstallAsync(
        InstallApp app, bool silent, IProgress<string>? log, CancellationToken ct)
    {
        var args = $"install --id {Quote(app.Id)} -e --accept-package-agreements --accept-source-agreements --disable-interactivity";
        if (silent) args += " --silent";

        log?.Report($"→ winget {args}");
        var (exit, output) = await RunAsync("winget", args, log, ct, TimeSpan.FromMinutes(30));
        return (exit == 0, output);
    }

    /// <summary>用 winget 卸载（桌面程序用，Appx 走下面的 RemoveAppAsync）。</summary>
    public static async Task<(bool Ok, string Output)> UninstallAsync(
        string id, IProgress<string>? log, CancellationToken ct)
    {
        var args = $"uninstall --id {Quote(id)} -e --disable-interactivity";
        log?.Report($"→ winget {args}");
        var (exit, output) = await RunAsync("winget", args, log, ct, TimeSpan.FromMinutes(15));
        return (exit == 0, output);
    }

    // ------------------------------------------------------------------ Appx
    /// <summary>
    /// 列出已安装应用。
    /// <para>
    /// <c>-PackageTypeFilter Main</c> 把框架包（VCLibs、.NET Native 之类）挡在外面：
    /// 那些包是别的应用依赖的，列出来让人勾选卸载等于埋雷。
    /// </para>
    /// <para>
    /// 预装包（Provisioned）是另一回事：它属于系统镜像，卸掉之后新用户登录不会再装回来。
    /// 这一步要管理员，没权限就跳过，不报错。
    /// </para>
    /// </summary>
    public static async Task<List<InstalledApp>> LoadAppsAsync(
        bool includeProvisioned, IProgress<string>? log, CancellationToken ct)
    {
        var result = new List<InstalledApp>();

        log?.Report("正在读取当前用户已安装的应用…");
        var ps = "Get-AppxPackage -PackageTypeFilter Main | ForEach-Object { " +
                 "\"$($_.Name)`t$($_.Version)`t$($_.PackageFullName)\" }";
        var (exit, output) = await RunPowerShellAsync(ps, null, ct, TimeSpan.FromSeconds(90));

        if (exit == 0)
        {
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length < 3) continue;
                result.Add(new InstalledApp(parts[0], parts[2], parts[1], "当前用户"));
            }
        }

        if (includeProvisioned)
        {
            log?.Report("正在读取系统预装包（需要管理员）…");
            var ps2 = "Get-AppxProvisionedPackage -Online | ForEach-Object { " +
                      "\"$($_.DisplayName)`t$($_.PackageName)\" }";
            var (exit2, output2) = await RunPowerShellAsync(ps2, null, ct, TimeSpan.FromSeconds(120));

            if (exit2 == 0)
            {
                foreach (var line in output2.Split('\n'))
                {
                    var parts = line.Trim().Split('\t');
                    if (parts.Length < 2) continue;
                    // 预装包没有版本列，包名里带着版本，取第二段当版本显示
                    result.Add(new InstalledApp(parts[0], parts[1], VersionFromPackageName(parts[1]), "系统预装"));
                }
            }
        }

        // 按名字排，中文按拼音序比较接近用户的预期
        return result
            .GroupBy(a => a.PackageFullName)
            .Select(g => g.First())
            .OrderBy(a => a.Source == "当前用户" ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static string VersionFromPackageName(string packageName)
    {
        // Microsoft.WindowsCalculator_11.2210.0.0_x64__8wekyb3d8bbwe → 11.2210.0.0
        var parts = packageName.Split('_');
        return parts.Length >= 2 ? parts[1] : "";
    }

    /// <summary>
    /// 不能卸的系统组件。
    ///
    /// <para>
    /// 这一条是安全底线。<c>Get-AppxPackage</c> 会把 <c>Microsoft.AAD.BrokerPlugin</c>
    /// （微软账户登录）、<c>Microsoft.BioEnrollment</c>（Windows Hello）、
    /// <c>windows.immersivecontrolpanel</c>（整个「设置」应用）都列出来，
    /// 它们跟商店应用长得一模一样，用户勾一下就可能把系统卸成砖。
    /// </para>
    /// <para>
    /// 判据用前缀匹配，不列全名：这些包名后面常带版本或语言段，写死全名很容易漏。
    /// 代价是可能误伤同前缀的普通应用，但宁可少卸，不可卸坏。
    /// </para>
    /// </summary>
    private static readonly string[] ProtectedPrefixes =
    {
        "Microsoft.AAD.",
        "Microsoft.AccountsControl",
        "Microsoft.BioEnrollment",
        "Microsoft.CredDialogHost",
        "Microsoft.LockApp",
        "Microsoft.UI.Xaml",
        "Microsoft.VCLibs",
        "Microsoft.NET.Native",
        "Microsoft.Services.Store",
        "Microsoft.StorePurchaseApp",
        "Microsoft.WindowsStore",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.WindowsAppRuntime",
        "Microsoft.SecHealthUI",
        "Microsoft.Win32WebViewHost",
        "Microsoft.Windows.CloudExperienceHost",
        "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.Search",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.XboxGameCallableUI",
        "Microsoft.XboxIdentityProvider",
        "Microsoft.MicrosoftEdge.Stable",
        "windows.immersivecontrolpanel",
        "Microsoft.Windows.Cortana",
    };

    /// <summary>这个应用是不是系统组件（界面上要标出来并禁止勾选）。</summary>
    public static bool IsProtectedApp(string name)
    {
        foreach (var prefix in ProtectedPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>卸载一个应用。预装包走 DISM 的 Remove-AppxProvisionedPackage。</summary>
    public static async Task<(bool Ok, string Output)> RemoveAppAsync(
        InstalledApp app, IProgress<string>? log, CancellationToken ct)
    {
        if (app.Source == "系统预装")
        {
            var ps = $"Remove-AppxProvisionedPackage -Online -PackageName '{app.PackageFullName}' -ErrorAction Stop | Out-Null; 'OK'";
            log?.Report($"→ Remove-AppxProvisionedPackage {app.PackageFullName}");
            var (exit, output) = await RunPowerShellAsync(ps, log, ct, TimeSpan.FromMinutes(10));
            return (exit == 0 && output.Contains("OK"), output);
        }
        else
        {
            var ps = $"Remove-AppxPackage -Package '{app.PackageFullName}' -ErrorAction Stop; 'OK'";
            log?.Report($"→ Remove-AppxPackage {app.Name}");
            var (exit, output) = await RunPowerShellAsync(ps, log, ct, TimeSpan.FromMinutes(10));
            return (exit == 0 && output.Contains("OK"), output);
        }
    }

    // ------------------------------------------------------------------ 还原点
    /// <summary>
    /// 创建系统还原点。
    /// <para>
    /// 两个坑：一是系统还原可能整个是关着的，得先 <c>Enable-ComputerRestore</c>；
    /// 二是 Windows 默认 24 小时内只让建一个点，第二次会直接报错 —— 这不是故障，
    /// 所以下面把这两种情况分开说明，别让用户以为坏了。
    /// </para>
    /// </summary>
    public static async Task<(bool Ok, string Message)> CreateRestorePointAsync(string description)
    {
        var ps = "try { " +
                 "  $d = (Get-CimInstance Win32_OperatingSystem).SystemDrive; " +
                 "  Enable-ComputerRestore -Drive \"$d\\\" -ErrorAction SilentlyContinue; " +
                 $"  Checkpoint-Computer -Description '{description}' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; " +
                 "  'OK' " +
                 "} catch { 'ERR ' + $_.Exception.Message }";

        var (_, output) = await RunPowerShellAsync(ps, null, CancellationToken.None, TimeSpan.FromMinutes(5));

        if (output.Contains("OK"))
            return (true, "还原点已创建。以后出问题可以在「系统属性 → 系统保护 → 系统还原」里回滚。");

        var msg = output.Trim();
        if (msg.Contains("0x80042316") || msg.Contains("frequency", StringComparison.OrdinalIgnoreCase))
            return (false, "Windows 限制 24 小时内只建一个还原点，今天已经建过了，等明天再试。");

        return (false, "创建失败：" + (msg.Length > 0 ? msg : "未知错误，可能需要管理员权限"));
    }

    // ------------------------------------------------------------------ 进程
    /// <summary>
    /// 跑一段 PowerShell 脚本。
    ///
    /// <para>
    /// <b>不拼 -Command，改成先落一个临时 .ps1 再 -File。</b>
    /// 原因：-Command 的整段脚本要经过两轮解析（先是 Windows 命令行拆参数，再是 PowerShell
    /// 自己），脚本里只要有一个引号、一个分号没对齐，结果就不是报错而是「PowerShell 静默地
    /// 等标准输入」—— 表现是进程挂住、不输出、不退出。踩过一次，排查花了半小时。
    /// </para>
    /// <para>
    /// 写文件时带 UTF-8 BOM：Windows PowerShell 5.1 读没有 BOM 的 .ps1 会按 ANSI 解码，
    /// 脚本里的中文描述就成乱码了。
    /// </para>
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunPowerShellAsync(
        string script, IProgress<string>? log, CancellationToken ct, TimeSpan timeout)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "win11toolbox_" + Guid.NewGuid().ToString("N") + ".ps1");

        var preamble = "[Console]::OutputEncoding=[Text.Encoding]::UTF8\r\n" +
                       "$ErrorActionPreference='Continue'\r\n";

        try
        {
            await System.IO.File.WriteAllTextAsync(path, preamble + script,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);

            var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"";
            return await RunAsync("powershell.exe", args, log, ct, timeout).ConfigureAwait(false);
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// 跑一个控制台程序，stdout/stderr 同时异步读，边读边回调。
    /// 返回值里的 Output 是两路合并后的全文，给调用方做判定用。
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(
        string file, string arguments, IProgress<string>? log, CancellationToken ct, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };

        using var process = new Process { StartInfo = psi };
        var buffer = new StringBuilder();
        var gate = new object();

        void Append(string? line)
        {
            if (line is null) return;
            lock (gate) buffer.AppendLine(line);
            log?.Report(line);
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时就杀进程，别留一个 winget 在后台继续装
            try { process.Kill(entireProcessTree: true); } catch { }
            lock (gate) buffer.AppendLine("（超时，已终止）");
            return (-1, buffer.ToString());
        }

        // 异步读的两个流在 WaitForExitAsync 之后可能还有尾巴，再等一次排空
        process.WaitForExit();

        int exit;
        lock (gate)
        {
            exit = process.HasExited ? process.ExitCode : -1;
            return (exit, buffer.ToString());
        }
    }
}
