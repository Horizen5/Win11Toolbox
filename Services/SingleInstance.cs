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
///
/// <para>
/// 2026-11-01 踩的坑：老实例在关闭时崩在 WPF 的遥测代码里（单文件发布下
/// <c>Assembly.Load("System.Diagnostics.Tracing, 10.0.0.0")</c> 解析失败），
/// 窗口已经销毁、进程却没退，互斥体一直攥在手里。之后每次双击都走到
/// 「锁在别人手上 → 去唤窗口 → 窗口是 0 → 安静退出」，用户看到的就是
/// **双击完全没反应，而且怎么点都没反应**。
/// 所以现在分三步：能唤起就唤起；唤不起来等它自己退；还不退就把它结束掉、接管这把锁。
/// </para>
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
            // 注意用 initiallyOwned: false + WaitOne，不要用 new Mutex(true, name, out created)。
            // 后者在「对象已存在但没人持有」时也会返回 created = false，白等一场；
            // WaitOne 遇到上一个进程崩掉留下的锁还会抛 AbandonedMutexException —— 那正是我们要的信号。
            _mutex = new Mutex(initiallyOwned: false, MutexName, out _);

            if (TryTakeLock(0))
            {
                WritePid();
                return true;
            }

            // 正常情况：确实已经开着，把那个窗口叫到前台，自己安静退出
            if (ActivateExisting())
            {
                DisposeMutex();
                return false;
            }

            // 唤不起来 —— 那个实例大概率已经废了：窗口没了、进程还挂着、锁不撒手。
            // 先给它两秒自己退（用户刚点关闭的时候会走这条路）。
            if (TryTakeLock(2000))
            {
                WritePid();
                return true;
            }

            // 还占着，而且连窗口都没有：这就是僵尸，结束它，把锁接过来继续启动
            if (KillStale() && TryTakeLock(3000))
            {
                WritePid();
                return true;
            }

            DisposeMutex();
            return false;
        }
        catch
        {
            // 创建互斥体失败（极端权限问题）就当没有锁，别把用户挡在门外
            return true;
        }
    }

    /// <summary>试着拿锁。上个实例崩掉留下的锁会抛 AbandonedMutexException —— 那等于拿到了。</summary>
    private static bool TryTakeLock(int millisecondsTimeout)
    {
        try
        {
            return _mutex != null && _mutex.WaitOne(millisecondsTimeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DisposeMutex()
    {
        try
        {
            _mutex?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _mutex = null;
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

    /// <summary>把已经在跑的那个窗口恢复并提到前台。找不到窗口返回 false。</summary>
    private static bool ActivateExisting()
    {
        try
        {
            if (!File.Exists(PidFile)) return false;
            if (!int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid)) return false;
            if (pid == Environment.ProcessId) return false;

            using var process = Process.GetProcessById(pid);
            process.Refresh();

            // 进程刚启动时窗口还没建出来，重试几次（最多约 2 秒）。
            // 不用 MainWindowHandle：它对「有窗口但不在任务栏」的情况不可靠，
            // 自己 EnumWindows 按 PID 找最大的可见顶层窗口更准。
            IntPtr handle = IntPtr.Zero;
            for (int i = 0; i < 20 && handle == IntPtr.Zero; i++)
            {
                handle = FindMainWindow((uint)pid);
                if (handle == IntPtr.Zero) Thread.Sleep(100);
            }

            if (handle == IntPtr.Zero) return false;

            if (IsIconic(handle))
                ShowWindow(handle, SW_RESTORE);

            // SetForegroundWindow 有前台锁限制，先 AttachThreadInput 更稳
            var foreground = GetForegroundWindow();
            uint fgThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            uint selfThread = GetCurrentThreadId();

            bool attached = false;
            if (fgThread != 0 && fgThread != selfThread)
                attached = AttachThreadInput(selfThread, fgThread, true);

            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(selfThread, fgThread, false);
            }

            return true;
        }
        catch
        {
            // 找不到就当作没有可用实例，交给上层决定要不要接管
            return false;
        }
    }

    /// <summary>
    /// 结束那个「锁还攥着、窗口却没了」的僵尸实例。
    /// 有可见窗口的一律不动 —— 宁可少杀，也不能把用户正开着的窗口干掉。
    /// </summary>
    private static bool KillStale()
    {
        try
        {
            if (!File.Exists(PidFile)) return true;      // 没线索，让上层再试一次锁就行
            if (!int.TryParse(File.ReadAllText(PidFile).Trim(), out int pid)) return true;
            if (pid == Environment.ProcessId) return true;

            using var process = Process.GetProcessById(pid);
            if (FindMainWindow((uint)pid) != IntPtr.Zero) return false;   // 有窗口，不能杀

            process.Kill();
            process.WaitForExit(3000);
            return true;
        }
        catch
        {
            // 进程已经没了也算成功
            return true;
        }
    }

    /// <summary>按 PID 找它最大的可见顶层窗口（跳过对话框和工具窗口）。</summary>
    private static IntPtr FindMainWindow(uint pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid) return true;
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if (!GetWindowRect(hwnd, out RECT r)) return true;

            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return best;
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
        }
        catch
        {
            // 忽略
        }

        DisposeMutex();
    }

    // ------------------------------------------------------------------ P/Invoke
    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

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
