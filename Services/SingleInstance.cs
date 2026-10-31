using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Win11Toolbox.Services;

/// <summary>
/// 单实例：第二次双击时不再开第二个窗口，而是把已经在跑的那个提到前台。
///
/// 为什么必须做：右键菜单页会把「已禁用」记到 disabled.tsv、把注册表导出到 backups\，
/// 两个实例同时写会互相覆盖，恢复的时候备份和记录对不上。
///
/// 认窗口的方式不是按进程名（exe 可以随便改名，比如 Win11工具箱.exe），
/// 而是让持有实例锁的进程把自己的 PID 写到 instance.pid，后来者读它再按 PID 找窗口。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\Win11Toolbox.SingleInstance";

    private static Mutex? _mutex;

    private static readonly string PidFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win11Toolbox", "instance.pid");

    /// <summary>拿到实例锁返回 true；已经有实例在跑返回 false（并顺手把它唤到前台）。</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
            if (created)
            {
                WritePid();
                return true;
            }

            _mutex.Dispose();
            _mutex = null;
            ActivateExisting();
            return false;
        }
        catch
        {
            // 创建互斥体失败（极端权限问题）就当没有锁，别把用户挡在门外
            return true;
        }
    }

    private static void WritePid()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PidFile)!);
            File.WriteAllText(PidFile, Environment.ProcessId.ToString());
        }
        catch
        {
            // 写不进去只影响「第二次启动能不能唤起窗口」，不影响主流程
        }
    }

    /// <summary>把已经在跑的那个窗口恢复并提到前台。</summary>
    private static void ActivateExisting()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            if (!int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid)) return;
            if (pid == Environment.ProcessId) return;

            using var process = Process.GetProcessById(pid);
            process.Refresh();

            var handle = process.MainWindowHandle;

            // 进程刚启动时窗口还没建出来，重试几次（最多约 1 秒）
            for (int i = 0; i < 10 && handle == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                process.Refresh();
                handle = process.MainWindowHandle;
            }

            if (handle == IntPtr.Zero) return;

            if (IsIconic(handle))
                ShowWindow(handle, SW_RESTORE);

            // SetForegroundWindow 有前台锁限制，先 AttachThreadInput 更稳
            var foreground = GetForegroundWindow();
            uint fgThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            uint selfThread = GetCurrentThreadId();

            if (fgThread != 0 && fgThread != selfThread)
                AttachThreadInput(selfThread, fgThread, true);

            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                if (fgThread != 0 && fgThread != selfThread)
                    AttachThreadInput(selfThread, fgThread, false);
            }
        }
        catch
        {
            // 找不到就安静退出，用户自己再点一次任务栏图标也一样
        }
    }

    /// <summary>正常退出时删掉 PID 文件，下次启动不会去找一个已经不存在的进程。</summary>
    public static void Release()
    {
        try
        {
            if (File.Exists(PidFile)) File.Delete(PidFile);
        }
        catch
        {
            // 忽略
        }

        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _mutex = null;
    }

    // ------------------------------------------------------------------ P/Invoke
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
