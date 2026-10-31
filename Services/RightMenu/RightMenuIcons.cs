// ================================================================
//  RightMenuIcons.cs —— 右键菜单列表的图标解析（带缓存）
//  图标解析链（沿用 v3.0 的顺序，修掉了"隐藏后变成盾牌"的老问题）：
//    1) 注册表 Icon 值（指向系统通用图标时不算数）
//    2) 命令行/关联文件里的主程序 exe —— 用 shell 关联图标
//    3) IconSpec 本身是个 exe
//    4) 同目录/附近的主程序（按公司名 + 产品名挑，跳过卸载更新类）
//    5) 系统通用图标（最后手段）
// ================================================================
using System;
using System.Collections.Generic;
using System.Windows.Media;

#nullable disable

namespace Win11Toolbox.RightMenu
{
    internal static class MenuIconProvider
    {
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        private static string KeyOf(RightMenuItem mi)
        {
            string spec = mi.IconSpec ?? "";
            string file = mi.TargetFile ?? "";
            return "app:" + spec + "|" + file;
        }

        /// <summary>归属软件的图标；取不到返回 null（界面就不画）。</summary>
        public static ImageSource Get(RightMenuItem mi)
        {
            if (mi == null) return null;
            string key = KeyOf(mi);
            if (Cache.TryGetValue(key, out var hit)) return hit;

            ImageSource icon = null;
            try { icon = Resolve(mi); } catch { icon = null; }
            if (icon == null) icon = GetCategory(mi.Category);

            Cache[key] = icon;
            return icon;
        }

        private static ImageSource Resolve(RightMenuItem mi)
        {
            string spec = mi.IconSpec ?? "";

            if (spec.Length > 0 && !IconLib.IsSystemIconSpec(spec))
            {
                var ic = IconLib.FromSpec(spec);
                if (ic != null) return ic;
            }

            string exe = mi.TargetFile ?? "";
            if (exe.Length > 0 && exe.ToLowerInvariant().EndsWith(".exe"))
            {
                var ic = IconLib.FromExe(exe);
                if (ic != null) return ic;
            }

            if (spec.Length > 0)
            {
                string f = Scanner.StripIndex(spec);
                if (f.ToLowerInvariant().EndsWith(".exe"))
                {
                    var ic = IconLib.FromExe(f);
                    if (ic != null) return ic;
                }
            }

            if (spec.Length > 0)
            {
                var ic = IconLib.FindNearbyAppIcon(Scanner.StripIndex(spec));
                if (ic != null) return ic;
            }

            if (spec.Length > 0)
            {
                var ic = IconLib.FromSpec(spec);
                if (ic != null) return ic;
            }

            return null;
        }

        /// <summary>位置图标：桌面 / 此电脑 / 回收站 / 文件夹 / 文件。</summary>
        public static ImageSource GetCategory(string category)
        {
            string c = category ?? "";
            string key = "cat:" + c;
            if (Cache.TryGetValue(key, out var hit)) return hit;

            ImageSource icon = null;
            try
            {
                if (c.Contains("回收站"))
                    icon = IconLib.FromSpec(Scanner.DefaultIconOf(Scanner.RecycleClsid));
                else if (c.Contains("此电脑"))
                    icon = IconLib.FromSpec(Scanner.DefaultIconOf(Scanner.ThisPcClsid));
                else if (c.Contains("桌面"))
                    icon = IconLib.FromSpec(Scanner.DefaultIconOf(Scanner.DesktopClsid));

                if (icon == null && (c.Contains("驱动器") || c.Contains("文件夹") ||
                                     c.Contains("目录") || c.Contains("空白")))
                    icon = IconLib.ShellStock("C:\\", IconLib.FILE_ATTRIBUTE_DIRECTORY);
                if (icon == null && (c.Contains("文件") || c.Contains("对象")))
                    icon = IconLib.ShellStock("a.txt", IconLib.FILE_ATTRIBUTE_NORMAL);
                if (icon == null)
                    icon = IconLib.ShellStock("a.exe", IconLib.FILE_ATTRIBUTE_NORMAL);
            }
            catch { icon = null; }

            Cache[key] = icon;
            return icon;
        }

        /// <summary>给「按软件」下拉框画小图标时用（暂未使用，留给后续）。</summary>
        public static void Clear() => Cache.Clear();
    }
}
