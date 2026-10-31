using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Win11Toolbox.Services;

/// <summary>
/// AMD 显卡风扇控制，直接调 ADL 的 OverdriveN 接口。
///
/// <para>
/// 为什么要自己写一遍：LibreHardwareMonitor 0.9.6 用的是 <c>ADL2_Overdrive5_FanSpeed_Set</c>
/// （老接口）。RDNA / RDNA2 上驱动对这个调用**返回成功但直接忽略** ——
/// 表现就是「滑块能拖、应用也提示成功、转速纹丝不动」。
/// 实测 RX 6700 XT：写 0% / 60% / 80% / 100%，读回永远是默认曲线那条。
/// </para>
/// <para>
/// 正确接口是 <c>ADL2_OverdriveN_FanControl_Get/Set</c>：
/// 先 Get 把结构体填满（顺便拿到驱动认的 iSize），改 <c>iFanControlMode = 1</c>（手动）
/// 和 <c>iTargetFanSpeed</c>，再 Set 回去。
/// </para>
/// </summary>
internal static class AmdFan
{
    private const string Dll = "atiadlxx.dll";
    private const int AdlOk = 0;
    private const int MaxPath = 256;

    // AdapterInfo 结构体大小：4+4+256 +4+4+4+4 +256+256 +4+4 +256+256+256 +4
    private const int AdapterInfoSize = 1572;

    // ADLODNFanControl：iSize + 8 个 int
    private const int FanControlSize = 36;

    private const int OffsetAdapterIndex = 4;
    private const int OffsetAdapterName = 280;

    // ---- 结构体字段偏移 ----
    private const int OffsetMode = 4;
    private const int OffsetFanControlMode = 8;
    private const int OffsetCurrentFanSpeedMode = 12;
    private const int OffsetCurrentFanSpeed = 16;
    private const int OffsetTargetFanSpeed = 20;
    private const int OffsetTargetTemperature = 24;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr MallocCallback(int size);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Create(IntPtr callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Main_Control_Destroy(IntPtr context);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int numAdapters);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int inputSize);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_OverdriveN_FanControl_Get(IntPtr context, int adapterIndex, IntPtr fanControl);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_OverdriveN_FanControl_Set(IntPtr context, int adapterIndex, IntPtr fanControl);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ADL2_Overdrive5_FanSpeedInfo_Get(IntPtr context, int adapterIndex, int displayIndex, IntPtr fanSpeedInfo);

    // 回调必须保持存活，否则 GC 之后 ADL 回调到野指针直接崩
    private static readonly MallocCallback Alloc = size => Marshal.AllocHGlobal(size);

    private static IntPtr _context;
    private static readonly object Gate = new();

    public sealed record Adapter(int Index, string Name);

    /// <summary>ADL 上下文。第一次用到时才创建。</summary>
    private static IntPtr Context
    {
        get
        {
            if (_context != IntPtr.Zero) return _context;

            try
            {
                var rc = ADL2_Main_Control_Create(
                    Marshal.GetFunctionPointerForDelegate(Alloc), 1, out var context);
                if (rc == AdlOk) _context = context;
            }
            catch
            {
                // 没装 AMD 驱动 / 不是 AMD 卡，这里会 DllNotFound 或 EntryPointNotFound
                _context = IntPtr.Zero;
            }

            return _context;
        }
    }

    public static bool Available => Context != IntPtr.Zero;

    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        var context = Context;
        if (context == IntPtr.Zero) return list;

        if (ADL2_Adapter_NumberOfAdapters_Get(context, out var count) != AdlOk || count <= 0)
            return list;

        var size = count * AdapterInfoSize;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (ADL2_Adapter_AdapterInfo_Get(context, buffer, size) != AdlOk)
                return list;

            for (var i = 0; i < count; i++)
            {
                var start = buffer + i * AdapterInfoSize;
                var index = Marshal.ReadInt32(start + OffsetAdapterIndex);
                var name = ReadString(start + OffsetAdapterName);
                if (!string.IsNullOrWhiteSpace(name))
                    list.Add(new Adapter(index, name));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return list;
    }

    private static string ReadString(IntPtr pointer)
    {
        var bytes = new List<byte>(64);
        for (var i = 0; i < MaxPath; i++)
        {
            var b = Marshal.ReadByte(pointer, i);
            if (b == 0) break;
            bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    /// <summary>读一次风扇控制块，顺便把结构体打印出来（诊断用）。</summary>
    public static (bool Ok, string Dump, int Percent, int Mode) Read(int adapterIndex)
    {
        var context = Context;
        if (context == IntPtr.Zero) return (false, "ADL 不可用", -1, -1);

        var buffer = Marshal.AllocHGlobal(FanControlSize);
        try
        {
            // 先写 iSize，Get 会按这个校验；驱动支持的版本会回填真实大小
            Marshal.WriteInt32(buffer, FanControlSize);
            var rc = ADL2_OverdriveN_FanControl_Get(context, adapterIndex, buffer);
            if (rc != AdlOk)
                return (false, $"Get 失败 rc={rc}", -1, -1);

            var size = Marshal.ReadInt32(buffer, 0);
            var mode = Marshal.ReadInt32(buffer, OffsetMode);
            var controlMode = Marshal.ReadInt32(buffer, OffsetFanControlMode);
            var currentMode = Marshal.ReadInt32(buffer, OffsetCurrentFanSpeedMode);
            var current = Marshal.ReadInt32(buffer, OffsetCurrentFanSpeed);
            var target = Marshal.ReadInt32(buffer, OffsetTargetFanSpeed);
            var targetTemp = Marshal.ReadInt32(buffer, OffsetTargetTemperature);

            var dump = $"iSize={size} iMode={mode} iFanControlMode={controlMode} "
                     + $"iCurrentFanSpeedMode={currentMode} iCurrentFanSpeed={current} "
                     + $"iTargetFanSpeed={target} iTargetTemperature={targetTemp}";

            return (true, dump, current, controlMode);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>设成手动 + 指定百分比。</summary>
    public static (bool Ok, string Message) SetPercent(int adapterIndex, int percent)
    {
        var context = Context;
        if (context == IntPtr.Zero) return (false, "ADL 不可用");

        var buffer = Marshal.AllocHGlobal(FanControlSize);
        try
        {
            Marshal.WriteInt32(buffer, FanControlSize);
            var rc = ADL2_OverdriveN_FanControl_Get(context, adapterIndex, buffer);
            if (rc != AdlOk) return (false, $"Get 失败 rc={rc}");

            // 保留驱动回填的其它字段，只改「手动模式」和目标转速
            Marshal.WriteInt32(buffer, OffsetFanControlMode, 1);
            Marshal.WriteInt32(buffer, OffsetTargetFanSpeed, Math.Clamp(percent, 0, 100));

            rc = ADL2_OverdriveN_FanControl_Set(context, adapterIndex, buffer);
            return rc == AdlOk
                ? (true, $"已设为手动 {percent}%")
                : (false, $"Set 失败 rc={rc}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>交回自动（驱动默认曲线）。</summary>
    public static (bool Ok, string Message) SetAuto(int adapterIndex)
    {
        var context = Context;
        if (context == IntPtr.Zero) return (false, "ADL 不可用");

        var buffer = Marshal.AllocHGlobal(FanControlSize);
        try
        {
            Marshal.WriteInt32(buffer, FanControlSize);
            var rc = ADL2_OverdriveN_FanControl_Get(context, adapterIndex, buffer);
            if (rc != AdlOk) return (false, $"Get 失败 rc={rc}");

            Marshal.WriteInt32(buffer, OffsetFanControlMode, 0);
            rc = ADL2_OverdriveN_FanControl_Set(context, adapterIndex, buffer);
            return rc == AdlOk ? (true, "已交回自动") : (false, $"Set 失败 rc={rc}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 探测驱动接受的 ADLODNFanControl 结构体大小。
    /// 不同 ADL 版本字段数不一样，大小不对会直接报错；扫一遍就知道驱动认哪个。
    /// </summary>
    public static List<string> ProbeStructSizes(int adapterIndex)
    {
        var results = new List<string>();
        var context = Context;
        if (context == IntPtr.Zero) return results;

        for (var size = 16; size <= 64; size += 4)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (var offset = 0; offset < size; offset += 4)
                    Marshal.WriteInt32(buffer, offset, 0);
                Marshal.WriteInt32(buffer, 0, size);

                var rc = ADL2_OverdriveN_FanControl_Get(context, adapterIndex, buffer);
                results.Add($"iSize={size,3} -> rc={rc}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return results;
    }

    /// <summary>读 Overdrive5 的风扇能力位（老接口），用来看驱动到底允许什么。</summary>
    public static string Od5FanInfo(int adapterIndex)
    {
        var context = Context;
        if (context == IntPtr.Zero) return "ADL 不可用";

        const int size = 36;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(buffer, size);
            var rc = ADL2_Overdrive5_FanSpeedInfo_Get(context, adapterIndex, 0, buffer);
            if (rc != AdlOk) return $"OD5 FanSpeedInfo rc={rc}";

            var flags = Marshal.ReadInt32(buffer, 4);
            var minPct = Marshal.ReadInt32(buffer, 8);
            var maxPct = Marshal.ReadInt32(buffer, 12);
            var defPct = Marshal.ReadInt32(buffer, 16);
            var minRpm = Marshal.ReadInt32(buffer, 20);
            var maxRpm = Marshal.ReadInt32(buffer, 24);
            var defRpm = Marshal.ReadInt32(buffer, 28);

            var can = new List<string>();
            if ((flags & 1) != 0) can.Add("百分比读");
            if ((flags & 2) != 0) can.Add("百分比写");
            if ((flags & 4) != 0) can.Add("RPM读");
            if ((flags & 8) != 0) can.Add("RPM写");

            return $"flags=0x{flags:X} [{string.Join("/", can)}] "
                 + $"百分比 {minPct}-{maxPct}（默认{defPct}） RPM {minRpm}-{maxRpm}（默认{defRpm}）";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 一句话说清这台机器上 ADL 风扇接口到底能不能用。
    /// 返回值直接拿去显示给用户，所以别写术语。
    /// </summary>
    public static string DescribeSupport()
    {
        if (!Available) return "找不到 AMD 显卡驱动接口（ADL）。";

        var radeon = Adapters().FirstOrDefault(a => a.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
        if (radeon is null) return "没找到 AMD 显卡。";

        var (ok, dump, _, _) = Read(radeon.Index);
        if (ok) return "ADL 风扇接口可用。";

        return dump.Contains("rc=-8", StringComparison.Ordinal)
            ? "AMD 驱动（ADL）已经不再开放软件调速接口（返回「不支持」），"
            + "显卡风扇只能用 Adrenalin 里的「自定义风扇曲线」来调。"
            : "ADL 风扇接口报错：" + dump;
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_context == IntPtr.Zero) return;
            try { ADL2_Main_Control_Destroy(_context); } catch { }
            _context = IntPtr.Zero;
        }
    }
}
