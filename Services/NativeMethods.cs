using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Win11Toolbox.Services;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static ulong _prevIdle, _prevKernel, _prevUser;

    /// <summary>整体 CPU 占用率（0-100），基于两次 GetSystemTimes 的差值。</summary>
    public static double CpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var i = idle.ToUInt64();
        var k = kernel.ToUInt64();
        var u = user.ToUInt64();

        var idleDelta = i - _prevIdle;
        var totalDelta = (k - _prevKernel) + (u - _prevUser);

        _prevIdle = i;
        _prevKernel = k;
        _prevUser = u;

        if (totalDelta == 0)
            return 0;

        return Math.Round((1.0 - (double)idleDelta / totalDelta) * 100.0, 1);
    }

    /// <summary>物理内存使用情况：(已用, 总量, 百分比)。</summary>
    public static (ulong Used, ulong Total, double Percent) Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (0, 0, 0);

        var used = status.TotalPhys - status.AvailPhys;
        var percent = status.TotalPhys == 0 ? 0 : Math.Round((double)used / status.TotalPhys * 100, 1);
        return (used, status.TotalPhys, percent);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcesses([Out] uint[] processIds, uint size, out uint needed);

    /// <summary>
    /// 进程数量。比 <c>Process.GetProcesses().Length</c> 快一个数量级 ——
    /// 后者要给每个进程建一个 <see cref="System.Diagnostics.Process"/> 对象并开句柄，
    /// 而这个只做一次内核查询，返回的就是一串 PID。
    /// 失败返回 -1（调用方决定怎么显示）。
    /// </summary>
    public static int ProcessCount()
    {
        try
        {
            var buffer = new uint[1024];
            while (true)
            {
                if (!EnumProcesses(buffer, (uint)(buffer.Length * sizeof(uint)), out var needed))
                    return -1;

                // 装得下就说明拿全了；正好装满则可能被截断，翻倍再问一次
                if (needed < buffer.Length * sizeof(uint))
                    return (int)(needed / sizeof(uint));

                if (buffer.Length >= 65536) return (int)(needed / sizeof(uint));
                buffer = new uint[buffer.Length * 2];
            }
        }
        catch
        {
            return -1;
        }
    }

    private const uint FirmwareTypeUefi = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>把某个窗口切到前台（诊断用：让别的程序盖到本程序上面）。</summary>
    public static bool BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        try { return SetForegroundWindow(hWnd); }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>
    /// 等一帧桌面合成完成。正常情况下每次 0~17ms；
    /// 明显变慢说明卡的是 DWM（整个桌面），而不是本程序的 UI 线程。
    /// </summary>
    public static void WaitForComposition()
    {
        try { DwmFlush(); } catch { /* 关掉 DWM 的环境没有这个导出 */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFirmwareType(out uint firmwareType);

    /// <summary>
    /// 启动模式：UEFI / Legacy BIOS / 未知。
    /// 早先靠「C:\Windows\Boot\EFI 在不在」猜，系统装在 D 盘或 EFI 分区没挂到那个路径就会误判；
    /// GetFirmwareType 是 Win8 起的官方接口，直接问固件。
    /// </summary>
    public static string FirmwareMode()
    {
        try
        {
            if (GetFirmwareType(out uint type))
                return type == FirmwareTypeUefi ? "UEFI" : "Legacy BIOS";
        }
        catch
        {
            // 老系统没有这个导出，走下面的兜底
        }

        try
        {
            return Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Boot", "EFI"))
                ? "UEFI"
                : "Legacy BIOS";
        }
        catch
        {
            return "—";
        }
    }
}
