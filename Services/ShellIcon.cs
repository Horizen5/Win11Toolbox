using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Win11Toolbox.Services;

/// <summary>取 exe / 文件夹的系统图标（和资源管理器里看到的一致）。</summary>
public static class ShellIcon
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiSmallIcon = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public int Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info,
                                               uint sizeInfo, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static ImageSource? FromPath(string path) => FromPath(path, small: false);

    /// <summary>
    /// 取图标。<paramref name="small"/>=true 时取系统「小图标」（约 16px），
    /// 用于列表行内的小图标，比把 32px 大图标缩下去更清晰。
    /// </summary>
    public static ImageSource? FromPath(string path, bool small)
    {
        try
        {
            var info = new ShFileInfo();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                                       ShgfiIcon | (small ? ShgfiSmallIcon : ShgfiLargeIcon));
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            DestroyIcon(info.Icon);
            return source;
        }
        catch
        {
            return null;
        }
    }
}
