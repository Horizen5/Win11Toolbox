using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Win11Toolbox.Services;

public sealed record StartupEntry(string Name, string Command, string Scope, string Source);

/// <summary>系统维护类操作：启动项、回收站、临时文件、网络、哈希、电源计划。</summary>
public static class ShellTools
{
    // .NET Core 起默认不带 GBK 这类 OEM 代码页，不注册的话 GetEncoding(936) 直接抛异常，
    // 电源计划名 / ipconfig 的中文就会乱码。这里在类型首次使用前注册一次。
    static ShellTools()
    {
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch { /* 注册失败就靠下面的兜底链 */ }
    }

    /// <summary>控制台程序输出用的编码：OEM 代码页优先，供诊断命令复用。</summary>
    internal static Encoding ConsoleEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            // 某些裁剪运行时里非 UTF-8 的代码页要注册 CodePagesEncodingProvider 才有，
            // 这里兜底：936 是简中 Windows 的 OEM 代码页，最后才退回 UTF-8
            try { return Encoding.GetEncoding(936); }
            catch { return Encoding.UTF8; }
        }
    }

    // ------------------------------------------------------------------ 启动项
    public static List<StartupEntry> StartupItems()
    {
        var items = new List<StartupEntry>();

        var keys = new (Microsoft.Win32.RegistryKey Hive, string Path, string Scope)[]
        {
            (Microsoft.Win32.Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "当前用户"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "所有用户"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "所有用户 (32 位)"),
        };

        foreach (var (hive, path, scope) in keys)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetValueNames())
                    items.Add(new StartupEntry(name, key.GetValue(name)?.ToString() ?? "", scope, "注册表"));
            }
            catch
            {
                // 忽略无权限的项
            }
        }

        var folders = new (string Path, string Scope)[]
        {
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                          @"Microsoft\Windows\Start Menu\Programs\Startup"), "当前用户"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                          @"Microsoft\Windows\Start Menu\Programs\Startup"), "所有用户"),
        };

        foreach (var (folder, scope) in folders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.GetFiles(folder))
            {
                // desktop.ini 是文件夹自带的配置，不是启动项；隐藏/系统文件同理，别列出来
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;
                }
                catch
                {
                    // 拿不到属性就当普通文件处理
                }

                items.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), file, scope, "启动文件夹"));
            }
        }

        return items;
    }

    /// <summary>启动项对应的图标来源路径（exe 或 .lnk）。</summary>
    public static string? ExecutablePath(StartupEntry item)
    {
        // 启动文件夹里的项，Command 就是 .lnk 的完整路径（可能带空格），不能按命令行切
        if (item.Source == "启动文件夹")
            return item.Command.Trim().Trim('"');

        return ExtractExecutable(item.Command);
    }

    /// <summary>从命令行里抠出可执行文件路径（处理带引号、带参数、裸路径几种写法）。</summary>
    public static string? ExtractExecutable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var text = command.Trim();

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }

        // 不带引号：截到 .exe 为止（路径里可能有空格，如 C:\Program Files\...）
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe >= 0)
            return text[..(exe + 4)];

        // 裸命令 / 其它扩展名：取第一个空格前
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    // ------------------------------------------------------------------ 回收站
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public uint Size;
        public long TotalSize;
        public long ItemCount;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref RecycleBinInfo info);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static (long Size, long Count) RecycleBin()
    {
        try
        {
            var info = new RecycleBinInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<RecycleBinInfo>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? (info.TotalSize, info.ItemCount) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static bool EmptyRecycleBin()
    {
        try
        {
            // 0x1 不确认 / 0x2 不显示进度 / 0x4 不播放声音
            return SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) == 0;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 临时文件
    public static (long Size, int Count) TempSize(bool includeSystem)
    {
        long total = 0;
        var count = 0;
        foreach (var folder in TempFolders(includeSystem))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in EnumerateFilesSafe(folder))
            {
                try
                {
                    total += new FileInfo(file).Length;
                    count++;
                }
                catch
                {
                    // 忽略
                }
            }
        }
        return (total, count);
    }

    public static (long Freed, int Deleted, int Skipped) CleanTemp(bool includeSystem)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;

        foreach (var folder in TempFolders(includeSystem))
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in EnumerateFilesSafe(folder))
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += length;
                    deleted++;
                }
                catch
                {
                    skipped++;
                }
            }
        }

        return (freed, deleted, skipped);
    }

    private static IEnumerable<string> TempFolders(bool includeSystem)
    {
        yield return Path.GetTempPath();
        if (includeSystem)
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
    }

    private static IEnumerable<string> EnumerateFilesSafe(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        using var enumerator = files.GetEnumerator();
        while (true)
        {
            string current;
            try
            {
                if (!enumerator.MoveNext()) yield break;
                current = enumerator.Current;
            }
            catch
            {
                yield break;
            }
            yield return current;
        }
    }

    // ------------------------------------------------------------------ 网络
    public static async Task<(bool Ok, string Message)> CheckPortAsync(string host, int port, int timeoutMs = 2000)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync(host, port);
            var finished = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            if (finished != connect)
            {
                // 超时后 connect 还在跑，客户端一关它就会以 SocketException 失败。
                // 没人 await 它 → TaskScheduler.UnobservedTaskException → 被写进 error.log，
                // 看起来像崩溃，其实只是超时。挂个续延把异常吃掉。
                _ = connect.ContinueWith(static t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                return (false, $"{host}:{port} 连接超时（可能被防火墙拦截）");
            }

            await connect;
            return (true, $"{host}:{port} 可连接（端口开放）");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return (false, ex.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => $"{host}:{port} 拒绝连接（端口未开放）",
                System.Net.Sockets.SocketError.HostNotFound => "无法解析主机名",
                _ => $"检测失败：{ex.Message}",
            });
        }
        catch (Exception ex)
        {
            return (false, $"检测失败：{ex.Message}");
        }
    }

    public static async Task<string> PingAsync(string host, int count = 4)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var builder = new StringBuilder();
            builder.AppendLine($"正在 Ping {host}，共 {count} 次：");
            for (var i = 0; i < count; i++)
            {
                var reply = await ping.SendPingAsync(host, 2000);
                builder.AppendLine(reply.Status == System.Net.NetworkInformation.IPStatus.Success
                    ? $"来自 {reply.Address} 的回复：时间 {reply.RoundtripTime} ms，TTL {reply.Options?.Ttl}"
                    : $"第 {i + 1} 次：{reply.Status}");
            }
            return builder.ToString();
        }
        catch (Exception ex)
        {
            return $"Ping 失败：{ex.Message}";
        }
    }

    public static string LocalIp()
    {
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            return host.AddressList.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "—";
        }
        catch
        {
            return "—";
        }
    }

    public static List<string> ResolveHost(string host)
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(host).Select(a => a.ToString()).Distinct().ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static bool FlushDns()
    {
        try
        {
            return Run("ipconfig", "/flushdns").ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void RestartExplorer()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
                process.Kill();
        }
        catch
        {
            // 忽略
        }

        try
        {
            Process.Start("explorer.exe");
        }
        catch
        {
            // 忽略
        }
    }

    public static void RefreshIconCache()
    {
        try
        {
            Run("ie4uinit.exe", "-show");
        }
        catch
        {
            // 忽略
        }
    }

    public static void RunElevated(string arguments, string? file = null)
    {
        Process.Start(new ProcessStartInfo(file ?? "cmd.exe", $"/k {arguments}")
        {
            UseShellExecute = true,
            Verb = "runas",
        });
    }

    // ------------------------------------------------------------------ 电源计划
    public static List<(string Guid, string Name, bool Active)> PowerPlans()
    {
        var plans = new List<(string, string, bool)>();
        try
        {
            var output = Run("powercfg", "/list").StandardOutput;
            foreach (var line in output.Split('\n'))
            {
                var match = Regex.Match(line,
                    @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\((.*?)\)");
                if (match.Success)
                    plans.Add((match.Groups[1].Value, match.Groups[2].Value.Trim(), line.Contains('*')));
            }
        }
        catch
        {
            // 忽略
        }
        return plans;
    }

    public static bool SetPowerPlan(string guid)
    {
        try
        {
            return Run("powercfg", $"/setactive {guid}").ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 文本工具
    public static string HashFile(string path, string algorithm)
    {
        using var stream = File.OpenRead(path);
        using HashAlgorithm hasher = algorithm.ToUpperInvariant() switch
        {
            "MD5" => MD5.Create(),
            "SHA1" => SHA1.Create(),
            "SHA512" => SHA512.Create(),
            _ => SHA256.Create(),
        };
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string Base64Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    public static string Base64Decode(string text)
    {
        try
        {
            var bytes = Convert.FromBase64String(text.Trim());
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return "（不是有效的 Base64 内容）";
        }
    }

    public static string UrlEncode(string text) => Uri.EscapeDataString(text);
    public static string UrlDecode(string text) => Uri.UnescapeDataString(text);

    public static string GeneratePassword(int length, bool upper, bool lower, bool digit, bool symbol)
    {
        var pools = new List<string>();
        if (upper) pools.Add("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        if (lower) pools.Add("abcdefghijklmnopqrstuvwxyz");
        if (digit) pools.Add("0123456789");
        if (symbol) pools.Add("!@#$%^&*()-_=+[]{};:,.?/");
        if (pools.Count == 0) pools.Add("abcdefghijklmnopqrstuvwxyz0123456789");

        var chars = new List<char>();
        foreach (var pool in pools)
            chars.Add(pool[RandomNumberGenerator.GetInt32(pool.Length)]);

        var all = string.Concat(pools);
        while (chars.Count < length)
            chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);

        return new string(chars.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(length).ToArray());
    }

    public static (int Score, string Label) PasswordStrength(string password)
    {
        var score = 0;
        if (password.Length >= 8) score++;
        if (password.Length >= 12) score++;
        if (password.Length >= 18) score++;
        if (Regex.IsMatch(password, "[a-z]") && Regex.IsMatch(password, "[A-Z]")) score++;
        if (Regex.IsMatch(password, "\\d")) score++;
        if (Regex.IsMatch(password, "[^A-Za-z0-9]")) score++;

        var labels = new[] { "极弱", "很弱", "较弱", "中等", "较强", "很强", "极强" };
        return (score, labels[Math.Min(score, 6)]);
    }

    // ------------------------------------------------------------------ 进程
    /// <summary>
    /// 跑一个控制台程序并取回它的标准输出。
    ///
    /// <para>
    /// <b>编码不能用 UTF-8</b>：<c>powercfg</c> / <c>ipconfig</c> 这类系统控制台程序输出的是
    /// <b>OEM 代码页</b>（简体中文系统上是 936/GBK），不是 UTF-8。
    /// 之前写死 <c>Encoding.UTF8</c>，电源计划名「卓越性能」「节能」全变成 <c>0u♦♦♦♦♦</c> 这种乱码。
    /// </para>
    /// <para>
    /// 这里取 <see cref="CultureInfo.CurrentCulture"/> 的 <c>OEMCodePage</c>，
    /// 即控制台真正在用的那个代码页。取不到就退回 936（简中默认），再不行才用 UTF-8。
    /// </para>
    /// </summary>
    private static (int ExitCode, string StandardOutput) Run(string file, string arguments)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ConsoleEncoding(),
        };

        using var process = new Process { StartInfo = psi };

        // stderr 必须一起排空。重定向了却只读 stdout，子进程往 stderr 写满 4 KB 管道缓冲区
        // 之后就会卡在 WriteFile 上，父进程在 ReadToEnd 里等它，两边互相等到超时。
        // 这里挂一个空处理器，只为了让管道有人读。
        process.ErrorDataReceived += static (_, _) => { };

        process.Start();
        process.BeginErrorReadLine();

        var output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit(15000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, output);
        }

        process.WaitForExit();      // 等异步读线程把尾巴排空
        return (process.ExitCode, output);
    }
}
