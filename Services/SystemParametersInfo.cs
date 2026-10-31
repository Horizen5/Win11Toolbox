using System.Runtime.InteropServices;

namespace Win11Toolbox.Services;

internal static class PowerStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>是否接着交流电（没有电池的台式机返回 true）。</summary>
    public static bool PowerOnline
    {
        get
        {
            if (!GetSystemPowerStatus(out var status))
                return true;

            return status.ACLineStatus != 0;   // 255 = 未知
        }
    }
}
