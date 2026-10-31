using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Threading;

namespace Win11Toolbox.Services;

public sealed class MonitorSnapshot
{
    public double Cpu { get; init; }
    public double MemoryPercent { get; init; }
    public ulong MemoryUsed { get; init; }
    public ulong MemoryTotal { get; init; }
    public double DiskPercent { get; init; }
    public ulong DiskFree { get; init; }
    public ulong DiskTotal { get; init; }
    public double NetDown { get; init; }
    public double NetUp { get; init; }
    public int ProcessCount { get; init; }
    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// 每秒采样一次，界面订阅 <see cref="Snapshot"/>。全部走系统 API，无第三方依赖。
///
/// <para>
/// 采样在**线程池**上做，不占 UI 线程。原因是实测这台机器上一次采样的耗时：
/// <c>Process.GetProcesses()</c> 3.6–5.4ms、<c>NetworkInterface.GetAllNetworkInterfaces()</c>
/// 9.8–16.7ms（23 张在用网卡），加起来每秒 15–22ms 白烧在 UI 线程上。
/// 平时看不出来，但正好赶上窗口重绘/切页时，这 20ms 就变成能感觉到的顿一下。
/// </para>
/// <para>
/// 改成后台线程后，UI 线程只负责最后把算好的数字塞进控件（<see cref="DispatcherPriority.Background"/>，
/// 有更急的活——重绘、输入——就先让路）。
/// </para>
/// </summary>
public sealed class SystemMonitor
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly string _systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C://";

    private Timer? _timer;
    private long _lastReceived;
    private long _lastSent;
    private DateTime _lastTick = DateTime.UtcNow;
    private int _busy;

    /// <summary>每次采样完成触发，**在 UI 线程上**回调，可以直接改控件。</summary>
    public event Action<MonitorSnapshot>? Snapshot;

    public void Start()
    {
        NativeMethods.CpuUsage();          // 预热，第一次差值无意义
        ReadNetwork(out _lastReceived, out _lastSent);
        _lastTick = DateTime.UtcNow;

        Stop();                            // 重复 Start 不要留下两个计时器
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Sample()
    {
        // 上一轮还没算完就跳过这一轮，别在慢机器上越堆越多
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            var snapshot = Take();
            _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => Snapshot?.Invoke(snapshot)));
        }
        catch
        {
            // 采样失败不该让程序崩，下一轮再试
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private MonitorSnapshot Take()
    {
        var cpu = NativeMethods.CpuUsage();
        var (used, total, memPercent) = NativeMethods.Memory();

        double diskPercent = 0;
        ulong free = 0, diskTotal = 0;
        try
        {
            var drive = new DriveInfo(_systemDrive);
            diskTotal = (ulong)drive.TotalSize;
            free = (ulong)drive.AvailableFreeSpace;
            diskPercent = diskTotal == 0 ? 0 : Math.Round((double)(diskTotal - free) / diskTotal * 100, 1);
        }
        catch
        {
            // 忽略：个别环境拿不到磁盘信息
        }

        ReadNetwork(out var received, out var sent);
        var now = DateTime.UtcNow;
        var seconds = Math.Max((now - _lastTick).TotalSeconds, 0.001);
        var down = Math.Max((received - _lastReceived) / seconds, 0);
        var up = Math.Max((sent - _lastSent) / seconds, 0);
        _lastReceived = received;
        _lastSent = sent;
        _lastTick = now;

        return new MonitorSnapshot
        {
            Cpu = cpu,
            MemoryPercent = memPercent,
            MemoryUsed = used,
            MemoryTotal = total,
            DiskPercent = diskPercent,
            DiskFree = free,
            DiskTotal = diskTotal,
            NetDown = down,
            NetUp = up,
            ProcessCount = CountProcesses(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    /// <summary>
    /// 进程数：优先 <c>EnumProcesses</c>（0.1–0.6ms），
    /// 它挂了才退回 <c>Process.GetProcesses()</c>（3.6–5.4ms，要给每个进程建对象）。
    /// </summary>
    private static int CountProcesses()
    {
        var fast = NativeMethods.ProcessCount();
        if (fast >= 0) return fast;

        try { return Process.GetProcesses().Length; }
        catch { return 0; }
    }

    private static void ReadNetwork(out long received, out long sent)
    {
        received = 0;
        sent = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                     n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var stats = nic.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch
        {
            // 忽略
        }
    }
}

public static class ByteFormatter
{
    public static string Format(ulong bytes, int precision = 1)
    {
        if (bytes == 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString($"F{precision}")} {units[unit]}";
    }
}
