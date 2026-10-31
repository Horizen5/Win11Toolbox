using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Win11Toolbox.RightMenu;

namespace Win11Toolbox.Services;

/// <summary>
/// 改计算机名。走系统自带的 Rename-Computer，和「设置 → 系统 → 重命名」是同一套机制：
/// 只写入待生效的名字，重启后才会真正改名。
/// </summary>
internal static class ComputerRename
{
    /// <summary>Windows 的计算机名规则：最长 15 个字符，不能全数字，不能有空格和特殊符号。</summary>
    public static bool IsValid(string name, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "名字不能为空。";
            return false;
        }

        name = name.Trim();
        if (name.Length > 15)
        {
            error = "最多 15 个字符。";
            return false;
        }

        if (!Regex.IsMatch(name, "^[A-Za-z0-9-]+$"))
        {
            error = "只能用英文字母、数字和连字符（不支持中文和空格）。";
            return false;
        }

        if (name.StartsWith("-", StringComparison.Ordinal) || name.EndsWith("-", StringComparison.Ordinal))
        {
            error = "连字符不能放在开头或结尾。";
            return false;
        }

        if (Regex.IsMatch(name, "^[0-9]+$"))
        {
            error = "名字不能全是数字。";
            return false;
        }

        return true;
    }

    /// <summary>改名的结果。ok=false 时 message 是失败原因。</summary>
    public readonly record struct Result(bool Ok, bool NeedReboot, string Message);

    public static Result Apply(string newName)
    {
        if (!IsValid(newName, out string error))
            return new Result(false, false, error);

        newName = newName.Trim();

        // 名字已经校验过只含字母数字连字符，拼进命令行不会被注入
        string command =
            $"Rename-Computer -NewName '{newName}' -Force -ErrorAction Stop; " +
            "Write-Output 'OK'";

        bool elevated = RightMenuActions.IsAdmin();

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        // 非管理员时弹一次 UAC，改名本身需要管理员权限
        if (!elevated)
            psi.Verb = "runas";

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new Result(false, false, "没能启动 PowerShell。");

            if (!process.WaitForExit(60000))
            {
                try { process.Kill(true); } catch { }
                return new Result(false, false, "等待超时，改名可能没有完成。");
            }

            if (process.ExitCode != 0)
                return new Result(false, false, $"改名失败（PowerShell 退出码 {process.ExitCode}）。");

            return new Result(true, true,
                $"已把计算机名改成 {newName}。\n\n和系统设置里改名一样，需要重启才会真正生效。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = 用户取消了 UAC
            return new Result(false, false, "已取消（没有获得管理员权限）。");
        }
        catch (Exception ex)
        {
            return new Result(false, false, "改名失败：" + ex.Message);
        }
    }
}
