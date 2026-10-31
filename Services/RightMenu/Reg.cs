// ================================================================
//  Reg.cs —— 注册表薄封装
//  从 RightMenuCore.cs 里拆出来的：轻松设置（EasySettingsCore）也要用它，
//  但那边不需要右键菜单的扫描 / .reg 导入导出，拆开之后「简洁版」才能只带上这 60 行。
// ================================================================
using System;
using Microsoft.Win32;

#nullable disable

namespace Win11Toolbox.RightMenu
{
    // ======================= 注册表工具 =======================
    internal static class Reg
    {
        public static RegistryKey Hive(string root)
        {
            if (string.Equals(root, "HKCU", StringComparison.OrdinalIgnoreCase)) return Registry.CurrentUser;
            if (string.Equals(root, "HKLM", StringComparison.OrdinalIgnoreCase)) return Registry.LocalMachine;
            // HKU 只在「RegLoadKey 加载出来的临时键」上用得到（RegLoadKey 不接受 HKCU）
            if (string.Equals(root, "HKU", StringComparison.OrdinalIgnoreCase)) return Registry.Users;
            return null;
        }

        public static string RootName(string root)
        {
            if (string.Equals(root, "HKCU", StringComparison.OrdinalIgnoreCase)) return "HKEY_CURRENT_USER";
            if (string.Equals(root, "HKU", StringComparison.OrdinalIgnoreCase)) return "HKEY_USERS";
            return "HKEY_LOCAL_MACHINE";
        }

        public static string NormRoot(string longName)
        {
            if (string.Equals(longName, "HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)) return "HKCU";
            if (string.Equals(longName, "HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)) return "HKLM";
            if (string.Equals(longName, "HKCU", StringComparison.OrdinalIgnoreCase)) return "HKCU";
            if (string.Equals(longName, "HKLM", StringComparison.OrdinalIgnoreCase)) return "HKLM";
            return null;
        }

        public static RegistryKey Open(string root, string path, bool writable)
        {
            RegistryKey h = Hive(root);
            if (h == null) return null;
            try { return h.OpenSubKey(path, writable); }
            catch { return null; }
        }

        public static bool Exists(string root, string path)
        {
            RegistryKey k = Open(root, path, false);
            if (k == null) return false;
            k.Close();
            return true;
        }

        public static string GetStr(RegistryKey k, string name)
        {
            if (k == null) return "";
            object v = k.GetValue(name, null);
            return v == null ? "" : v.ToString();
        }

        public static bool IsWindowsPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            string s;
            try { s = Environment.ExpandEnvironmentVariables(p); }
            catch { s = p; }
            s = s.ToLowerInvariant().Trim('"');
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            if (s.StartsWith(win)) return true;
            if (s.Contains("\\windows\\")) return true;
            if (s.StartsWith("\\systemroot\\")) return true;
            if (s.StartsWith("%systemroot%")) return true;
            return false;
        }
    }
}
