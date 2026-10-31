// ================================================================
//  EasySettingsCore.cs —— 系统轻松设置 · 逻辑层
//  由「右键菜单管家 v3.0」的轻松设置模块移植，去掉 WinForms。
//  65 项安全设置，每项都带 Probe（当前状态）/ Set（应用或恢复默认）。
//  开关清单参照「Windows 11 轻松设置」（免费软件）等工具整理，代码为自行实现。
// ================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Win11Toolbox.RightMenu;

#nullable disable

namespace Win11Toolbox.SettingsKit
{
    internal class SettingItem
    {
        public string Category = "";
        public string Name = "";
        public string Desc = "";          // 勾选后会发生什么 / 恢复默认时是什么
        public bool NeedAdmin;            // 需要管理员权限
        public bool NeedExplorerRestart;  // 需要重启资源管理器后生效
        public Func<bool> Probe;          // 当前是否处于「已勾选」状态
        public Action<bool> Set;          // true=应用设置 false=恢复默认
        public string RegPath = "";       // 涉及的主要注册表路径（展示用，空=非注册表项）
        public string Risk = "";          // 非空 = 危险项，勾选前要弹确认，内容就是确认框里的说明
        public string StateText = "";     // 最近一次探测后的状态文字
        public bool StateOk = true;       // 探测是否成功

        /// <summary>
        /// 非空 = 这一项系统不让程序改，勾选时不要硬写，直接引导用户去这个位置手动改
        /// （<c>ms-settings:taskbar</c> 这类）。目前只有「隐藏任务栏搜索框」用得上。
        /// </summary>
        public string GuideUrl = "";

        public bool Danger => Risk.Length > 0;
    }

    // ======================= 数据：全部设置项 =======================
    internal static class EasySettingsData
    {
        /// <summary>最近一次打开快捷入口失败的原因（界面读它弹 InfoBar）。</summary>
        public static string LastError = "";

        // ---------------- 注册表小工具 ----------------
        static int? ReadDword(string root, string path, string name)
        {
            try
            {
                RegistryKey k = Reg.Open(root, path, false);
                if (k == null) return null;
                try
                {
                    object v = k.GetValue(name, null);
                    if (v == null) return null;
                    if (v is int) return (int)v;
                    int n;
                    return int.TryParse(v.ToString(), out n) ? (int?)n : null;
                }
                finally { k.Close(); }
            }
            catch { return null; }
        }

        /// <summary>读 REG_BINARY；不存在或读不到返回 null。</summary>
        static byte[] ReadBinary(string root, string path, string name)
        {
            try
            {
                RegistryKey k = Reg.Open(root, path, false);
                if (k == null) return null;
                try { return k.GetValue(name, null) as byte[]; }
                finally { k.Close(); }
            }
            catch { return null; }
        }

        /// <summary>读 REG_SZ / REG_EXPAND_SZ；不存在或读不到返回 null。</summary>
        static string ReadString(string root, string path, string name)
        {
            try
            {
                RegistryKey k = Reg.Open(root, path, false);
                if (k == null) return null;
                try
                {
                    object v = k.GetValue(name, null);
                    return v == null ? null : v.ToString();
                }
                finally { k.Close(); }
            }
            catch { return null; }
        }

        static void WriteDword(string root, string path, string name, int val)
        {
            RegistryKey k = Reg.Hive(root).CreateSubKey(path, RegistryKeyPermissionCheck.Default);
            if (k == null) throw new InvalidOperationException("打不开 " + root + "\\" + path);
            try { k.SetValue(name, val, RegistryValueKind.DWord); }
            finally { k.Close(); }
        }

        /// <summary>
        /// 写 REG_SZ。
        ///
        /// 不能图省事用 WriteExpandString：REG_EXPAND_SZ 里的 %...% 会被系统当环境变量
        /// 展开，而「新建快捷方式模板」的值恰好是 <c>%s.lnk</c> —— 展开失败后模板就废了。
        /// </summary>
        static void WriteString(string root, string path, string name, string val)
        {
            RegistryKey k = Reg.Hive(root).CreateSubKey(path, RegistryKeyPermissionCheck.Default);
            if (k == null) throw new InvalidOperationException("打不开 " + root + "\\" + path);
            try { k.SetValue(name, val, RegistryValueKind.String); }
            finally { k.Close(); }
        }

        static void WriteExpandString(string root, string path, string name, string val)
        {
            RegistryKey k = Reg.Hive(root).CreateSubKey(path, RegistryKeyPermissionCheck.Default);
            if (k == null) throw new InvalidOperationException("打不开 " + root + "\\" + path);
            try { k.SetValue(name, val, RegistryValueKind.ExpandString); }
            finally { k.Close(); }
        }

        static void WriteBinary(string root, string path, string name, byte[] val)        {
            RegistryKey k = Reg.Hive(root).CreateSubKey(path, RegistryKeyPermissionCheck.Default);
            if (k == null) throw new InvalidOperationException("打不开 " + root + "\\" + path);
            try { k.SetValue(name, val, RegistryValueKind.Binary); }
            finally { k.Close(); }
        }

        static void DeleteValue(string root, string path, string name)
        {
            try
            {
                RegistryKey k = Reg.Hive(root).OpenSubKey(path, true);
                if (k == null) return;
                try { k.DeleteValue(name, false); }
                finally { k.Close(); }
            }
            catch { }
        }

        static void DeleteKeyTree(string root, string path)
        {
            try
            {
                RegistryKey h = Reg.Hive(root);
                int i = path.LastIndexOf('\\');
                if (i <= 0) { h.DeleteSubKeyTree(path, false); return; }
                RegistryKey parent = h.OpenSubKey(path.Substring(0, i), true);
                if (parent == null) return;
                try { parent.DeleteSubKeyTree(path.Substring(i + 1), false); }
                finally { parent.Close(); }
            }
            catch { }
        }

        /// <summary>
        /// 实测某个键到底能不能写 —— 写一个临时值再删掉。
        ///
        /// <para>
        /// 不能靠看 DACL 判断：<c>HKCU\...\CurrentVersion\Search</c> 的 DACL 里明明有
        /// <c>Administrators = KEY_ALL_ACCESS</c>，但 <c>SetValue</c> / <c>CreateSubKey</c>
        /// 都返回 <c>0x80070005</c>（拒绝访问），连以管理员（High 完整性）运行也一样，
        /// 换成 <c>HKEY_USERS\&lt;SID&gt;</c> 直连同一个物理键也照样拒。
        /// 那个键上有一条只给 Windows 搜索 AppContainer 的 ACE，实际写入由系统挡着。
        /// </para>
        /// <para>
        /// 所以只能试。试完立刻删掉临时值，不留痕迹。
        /// </para>
        /// </summary>
        static bool CanWriteKey(string root, string path)
        {
            try
            {
                RegistryKey k = Reg.Open(root, path, true);
                if (k == null) return false;
                try
                {
                    k.SetValue("__wtest__", 1, RegistryValueKind.DWord);
                    k.DeleteValue("__wtest__", false);
                    return true;
                }
                catch { return false; }
                finally { k.Close(); }
            }
            catch { return false; }
        }

        /// <summary>搜索设置的键。Win11 上整片被锁（见 CanWriteKey）。</summary>
        const string SEARCH_KEY = "Software\\Microsoft\\Windows\\CurrentVersion\\Search";

        // ---------------- 注册表权限修复 ----------------
        //
        // 有些系统键的 DACL 看着完全正常（Administrators = KEY_ALL_ACCESS），实际写入却返回
        // 0x80070005。Win11 的 CurrentVersion\Search 就是这种：管理员、32 位视图、HKEY_USERS
        // 直连、Registry.SetValue 静态方法、New-ItemProperty 全都试过，一律拒绝。
        //
        // 唯一还能走的路是：先拿到这个键的所有权，再补一条给当前用户的 ACE，然后写。
        // 写完必须把原 SDDL 原样写回去，否则会在用户的注册表里留下一个被改过权限的键。
        //
        // 这套东西只在「正常写入失败」时才触发，平时碰都不碰。

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool LookupPrivilegeValueW(string system, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr prev, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // 直接用 Win32 注册表 API 再测一遍，拿到确切错误码（.NET 的异常信息太笼统）
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegOpenKeyExW(IntPtr hKey, string subKey, uint options, int samDesired, out IntPtr result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegSetValueExW(IntPtr hKey, string valueName, uint reserved, uint type,
            byte[] data, int cbData);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegCloseKey(IntPtr hKey);

        static readonly IntPtr HKEY_CURRENT_USER = (IntPtr)unchecked((int)0x80000001);
        static readonly IntPtr HKEY_USERS = (IntPtr)unchecked((int)0x80000003);
        const int KEY_QUERY_VALUE = 0x0001;
        const int KEY_SET_VALUE = 0x0002;
        const uint REG_DWORD = 4;

        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x0002;

        /// <summary>启用当前进程的一个特权（如 SeTakeOwnershipPrivilege）。</summary>
        static bool EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;
            try
            {
                LUID luid;
                if (!LookupPrivilegeValueW(null, name, out luid)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Privileges.Luid = luid;
                tp.Privileges.Attributes = SE_PRIVILEGE_ENABLED;
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                // AdjustTokenPrivileges 会「成功返回但一个都没改」，必须查 LastError
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }

        static NTAccount CurrentUser()
        {
            return new NTAccount(Environment.UserDomainName, Environment.UserName);
        }

        // ---- 整键还原用到的 API ----
        //
        // 搜索键的「写值」被系统挡死：RegSetValueEx 一律 ERROR_ACCESS_DENIED，
        // 管理员跑、SYSTEM 跑、FullControl 句柄、RegCopyTree 全都被拒，
        // 可同一个句柄上 CreateSubKey / DeleteValue 却都放行。
        // 只有存盘 → 挂载 → 改副本 → 卸载 → RegRestoreKey 这条路走得通。

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegSaveKeyW(IntPtr hKey, string file, IntPtr sa);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegRestoreKeyW(IntPtr hKey, string file, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegLoadKeyW(IntPtr hKey, string subKey, string file);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegUnLoadKeyW(IntPtr hKey, string subKey);

        const int KEY_ALL_ACCESS = 0x000F003F;

        const int REG_FORCE_RESTORE = 0x00000008;
        static readonly IntPtr HKEY_LOCAL_MACHINE = (IntPtr)unchecked((int)0x80000002);

        /// <summary>
        /// 设置任务栏搜索框模式：0 = 隐藏，1 = 只显示图标，2 = 显示搜索框。
        ///
        /// <para>
        /// 走 <see cref="MutateProtectedKey"/> 这条整键还原通道 —— 直接写值会被系统拦掉。
        /// 顺便把之前调试留下的 <c>__stage_marker__</c> 一起清掉。
        /// </para>
        /// </summary>
        internal static bool SetTaskbarSearchMode(int mode, out string error)
        {
            error = "";

            // 能直接写的系统（大多数）就走直写：快，也不会碰整键还原那套副作用
            if (SearchKeyWritable())
            {
                try
                {
                    WriteDword("HKCU", SEARCH_KEY, "SearchboxTaskbarMode", mode);
                    if (mode == 0) WriteDword("HKCU", SEARCH_KEY, "SearchboxTaskbarModeCache", 1);
                    else DeleteValue("HKCU", SEARCH_KEY, "SearchboxTaskbarModeCache");
                }
                catch (Exception ex) { error = ex.GetType().Name + "：" + ex.Message; }

                int? direct = ReadDword("HKCU", SEARCH_KEY, "SearchboxTaskbarMode");
                if (direct.HasValue && direct.Value == mode) return true;
                error = "";
            }

            // 写值被系统拦掉的版本（实测 Windows 11 26H2）：只能整键还原
            int m = mode;
            bool ok = MutateProtectedKey("HKCU", SEARCH_KEY, k =>
            {
                k.SetValue("SearchboxTaskbarMode", m, RegistryValueKind.DWord);
                // Cache 只是给外壳的提示值，隐藏时补上、显示时清掉，跟系统设置的行为一致
                if (m == 0) k.SetValue("SearchboxTaskbarModeCache", 1, RegistryValueKind.DWord);
                else k.DeleteValue("SearchboxTaskbarModeCache", false);
                k.DeleteValue("__stage_marker__", false);
            }, out error);

            if (!ok) return false;
            int? back = ReadDword("HKCU", SEARCH_KEY, "SearchboxTaskbarMode");
            if (!back.HasValue || back.Value != m)
            {
                error = "写入后读回来是 " + Show(back) + "，不是 " + m;
                return false;
            }
            // 整键还原把外壳挂在这个键上的监听弄断了：先造一次注册表变更通知让它重读
            // （这才能把搜索框重新建出来），再补一次广播。不用重启资源管理器。
            KickShell("HKCU", SEARCH_KEY);
            return true;
        }

        /// <summary>
        /// 往「值写入被系统拦掉」的键里写东西。目前只有 <c>CurrentVersion\Search</c> 系列是这种。
        ///
        /// <para>
        /// 为什么不能直接写：<c>RegSetValueEx</c> 一律返回 ERROR_ACCESS_DENIED —— 管理员跑、
        /// SYSTEM 跑、用 FullControl 句柄打开、换 <c>RegCopyTree</c>，全部一样被拒；可同一个句柄上
        /// <c>CreateSubKey</c> 和 <c>DeleteValue</c> 偏偏都成功。也就是说拦的不是 DACL、不是
        /// 完整性级别（这键连 SACL 都没有），而是「往这个键写值」这个动作本身。
        /// </para>
        /// <para>
        /// 走得通的是整键还原：存盘 → 挂成 <c>HKU\&lt;临时名&gt;</c> → 在副本上随便改（副本不受保护）
        /// → 卸载 → <c>RegRestoreKey</c> 换回去。还原必须带 <c>REG_FORCE_RESTORE</c>，
        /// 否则因为键正被搜索服务占着，一样返回拒绝访问。
        /// </para>
        /// <para>
        /// 不破坏原有权限：存盘文件里带着原键完整的描述符，还原后 DACL 跟原来一模一样，
        /// 不会在用户注册表里留下被放宽过权限的键。临时键、临时文件在 finally 里清掉。
        /// </para>
        /// </summary>
        internal static bool MutateProtectedKey(string root, string path, Action<RegistryKey> mutate, out string error)
        {
            error = "";
            IntPtr hTarget = IntPtr.Zero;
            string origSddl = null;
            string tempName = "WbTmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string dir = Path.Combine(Path.GetTempPath(), "Win11Toolbox", "hive");
            string hiv = Path.Combine(dir, tempName + ".hiv");
            bool loaded = false;
            try
            {
                EnablePrivilege("SeBackupPrivilege");
                EnablePrivilege("SeRestorePrivilege");
                Directory.CreateDirectory(dir);

                IntPtr hRoot = string.Equals(root, "HKLM", StringComparison.OrdinalIgnoreCase)
                    ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER;
                int rc = RegOpenKeyExW(hRoot, path, 0, KEY_ALL_ACCESS, out hTarget);
                if (rc != 0) { error = "打不开键（" + Win32Msg(rc) + "）"; return false; }

                // RegSaveKey 不保存安全描述符 —— 还原之后这个键的 DACL 会变成默认值，
                // 原来那条给搜索能力的 ACE 就没了。所以先抄一份，还原完立刻补回去。
                origSddl = ReadSddl(root, path);

                rc = RegSaveKeyW(hTarget, hiv, IntPtr.Zero);
                if (rc != 0) { error = "备份键失败（" + Win32Msg(rc) + "）"; return false; }

                rc = RegLoadKeyW(HKEY_USERS, tempName, hiv);
                if (rc != 0) { error = "加载临时键失败（" + Win32Msg(rc) + "）"; return false; }
                loaded = true;

                if (!ApplyToTempKey(tempName, mutate, out error)) return false;
                SweepTempSubKeys(tempName);
                // 先卸载把改动写回 hiv，再整键还原
                rc = RegUnLoadKeyW(HKEY_USERS, tempName);
                loaded = false;
                if (rc != 0) { error = "卸载临时键失败（" + Win32Msg(rc) + "）"; return false; }

                rc = RegRestoreKeyW(hTarget, hiv, REG_FORCE_RESTORE);
                if (rc != 0) { error = "还原键失败（" + Win32Msg(rc) + "）"; return false; }

                if (!string.IsNullOrEmpty(origSddl) && !RestoreAcl(root, path, origSddl, out string aclErr))
                {
                    // 值已经写进去了，但权限没补回来 —— 这比写失败更严重，得让调用方知道
                    error = "值已写入，但恢复键权限失败：" + aclErr;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
            finally
            {
                if (loaded) RegUnLoadKeyW(HKEY_USERS, tempName);
                if (hTarget != IntPtr.Zero) RegCloseKey(hTarget);
                try { if (File.Exists(hiv)) File.Delete(hiv); } catch { }
                try { Registry.Users.DeleteSubKeyTree(tempName, false); } catch { }
            }
        }

        /// <summary>
        /// 清掉「踢一下」留下的临时子键（<c>__</c> 开头）。
        /// 在加载出来的副本上删，随 <c>RegRestoreKey</c> 一起生效 ——
        /// 直接删真键上的子键没有权限（建得出来、删不掉）。
        /// </summary>
        static void SweepTempSubKeys(string tempName)
        {
            try
            {
                using (RegistryKey k = Registry.Users.OpenSubKey(tempName, true))
                {
                    if (k == null) return;
                    foreach (string n in k.GetSubKeyNames())
                    {
                        if (!n.StartsWith("__", StringComparison.Ordinal)) continue;
                        try { k.DeleteSubKeyTree(n, false); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>读一个键完整的安全描述符（SDDL 字符串），读不到返回 null。</summary>
        static string ReadSddl(string root, string path)
        {
            try
            {
                using (RegistryKey k = Reg.Hive(root).OpenSubKey(path, false))
                {
                    if (k == null) return null;
                    return k.GetAccessControl(AccessControlSections.All)
                            .GetSecurityDescriptorSddlForm(AccessControlSections.All);
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 把键的权限恢复成指定 SDDL。
        /// 整键还原之后这个键的 DACL 已经是默认值，我们没有 WRITE_DAC 了，
        /// 所以得先接管所有权（管理员靠 SeTakeOwnershipPrivilege 可以无视 DACL 拿走），
        /// 再写回原 SDDL —— 写回去的同时所有者也会变回原来的 SYSTEM。
        /// </summary>
        static bool RestoreAcl(string root, string path, string sddl, out string error)
        {
            error = "";
            if (GrantWriteAccess(root, path, out error) == null) return false;
            return RestoreKeySecurity(root, path, sddl, out error);
        }

        /// <summary>在加载出来的副本上执行改动。副本的 DACL 是从原键带过来的，必要时临时接管。</summary>
        static bool ApplyToTempKey(string tempName, Action<RegistryKey> mutate, out string error)
        {
            error = "";
            try
            {
                using (RegistryKey k = Registry.Users.OpenSubKey(tempName, true))
                {
                    if (k == null) { error = "打不开临时键"; return false; }
                    mutate(k);
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // 副本跟着原键一起被锁的情况：接管权限 → 改 → 立刻还原，免得被 RegRestoreKey 写回真键
                string sddl = GrantWriteAccess("HKU", tempName, out string e2);
                if (sddl == null) { error = "临时键不可写：" + e2; return false; }
                try
                {
                    using (RegistryKey k = Registry.Users.OpenSubKey(tempName, true))
                    {
                        if (k == null) { error = "打不开临时键"; return false; }
                        mutate(k);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + "：" + ex.Message;
                    return false;
                }
                finally { RestoreKeySecurity("HKU", tempName, sddl, out _); }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        static string Show(int? v) => v.HasValue ? v.Value.ToString() : "(不存在)";

        /// <summary>
        /// 拿到键的所有权并给当前用户完全控制。
        /// 成功时返回原来的 SDDL 字符串（调用方写完值后必须用它恢复），失败返回 null。
        /// </summary>
        internal static string GrantWriteAccess(string root, string path, out string error)
        {
            error = "";
            if (!EnablePrivilege("SeTakeOwnershipPrivilege") || !EnablePrivilege("SeRestorePrivilege"))
            {
                error = "启用 SeTakeOwnershipPrivilege / SeRestorePrivilege 失败（需要以管理员身份运行）";
                return null;
            }

            string backup = null;
            try
            {
                // 第一步：备份原 SDDL
                using (RegistryKey k = Reg.Hive(root).OpenSubKey(path,
                    RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions))
                {
                    if (k == null) { error = "打不开键（读权限）"; return null; }
                    backup = k.GetAccessControl(AccessControlSections.All)
                              .GetSecurityDescriptorSddlForm(AccessControlSections.All);
                }

                // 第二步：把所有者改成当前用户
                using (RegistryKey k = Reg.Hive(root).OpenSubKey(path,
                    RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership))
                {
                    if (k == null) { error = "打不开键（WRITE_OWNER）"; return null; }
                    RegistrySecurity sec = k.GetAccessControl(AccessControlSections.Owner);
                    sec.SetOwner(CurrentUser());
                    k.SetAccessControl(sec);
                }

                // 第三步：补一条完全控制的 ACE
                using (RegistryKey k = Reg.Hive(root).OpenSubKey(path,
                    RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions))
                {
                    if (k == null) { error = "打不开键（WRITE_DAC）"; return null; }
                    RegistrySecurity sec = k.GetAccessControl(AccessControlSections.Access);
                    sec.AddAccessRule(new RegistryAccessRule(CurrentUser(), RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                    k.SetAccessControl(sec);
                }

                return backup;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                // 中途失败也要尽量把原 SDDL 写回去
                if (backup != null) RestoreKeySecurity(root, path, backup, out _);
                return null;
            }
        }

        /// <summary>把 SDDL（含所有者）原样写回键上。</summary>
        internal static bool RestoreKeySecurity(string root, string path, string sddl, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(sddl)) { error = "SDDL 为空"; return false; }
            try
            {
                EnablePrivilege("SeRestorePrivilege");   // 把所有者改回 SYSTEM 需要它
                using (RegistryKey k = Reg.Hive(root).OpenSubKey(path,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ChangePermissions | RegistryRights.TakeOwnership | RegistryRights.ReadPermissions))
                {
                    if (k == null) { error = "打不开键（恢复权限）"; return false; }
                    RegistrySecurity sec = new RegistrySecurity();
                    sec.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.All);
                    k.SetAccessControl(sec);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>把 Win32 错误码翻译成人话。注册表 API 返回的是 Win32 错误码（ERROR_xxx）。</summary>
        internal static string Win32Msg(int code)
        {
            if (code == 0) return "成功";
            // RegXxx 系列返回的是 Win32 错误码（ERROR_xxx），不是 NTSTATUS
            switch (code)
            {
                case 5: return "0x5 ERROR_ACCESS_DENIED 拒绝访问";
                case 2: return "0x2 ERROR_FILE_NOT_FOUND 键不存在";
                case 87: return "0x57 ERROR_INVALID_PARAMETER 参数不对";
                case 6: return "0x6 ERROR_INVALID_HANDLE 句柄无效";
                case 8: return "0x8 ERROR_NOT_ENOUGH_MEMORY";
                case 1314: return "0x522 ERROR_PRIVILEGE_NOT_HELD 缺特权";
            }
            return "0x" + code.ToString("X") + " " + new System.ComponentModel.Win32Exception(code).Message;
        }

        /// <summary>只测一次：进页面、跑诊断都会调 Build()，没必要每次都写一遍临时值。</summary>
        static bool? _searchWritable;

        static bool SearchKeyWritable()
        {
            if (!_searchWritable.HasValue)
                _searchWritable = CanWriteKey("HKCU", SEARCH_KEY);
            return _searchWritable.Value;
        }

        static string P(string root, string path, string val)
        {
            return root + "\\" + path + (val.Length > 0 ? " → " + val : "");
        }

        // 常规 DWORD 项：onVal = 勾选时写入；offVal = 取消时写入（offVal < 0 表示删除该值）
        static SettingItem Dword(string cat, string name, string desc, string root, string path, string val,
            int onVal, int offVal, bool admin, bool restart, string risk = "")
        {
            SettingItem it = new SettingItem();
            it.Category = cat;
            it.Name = name;
            it.Desc = desc;
            it.NeedAdmin = admin;
            it.NeedExplorerRestart = restart;
            it.Risk = risk;
            it.RegPath = P(root, path, val + " = " + onVal + "（默认 " + (offVal < 0 ? "无此值" : offVal.ToString()) + "）");
            it.Probe = delegate
            {
                int? v = ReadDword(root, path, val);
                return v.HasValue && v.Value == onVal;
            };
            it.Set = delegate(bool on)
            {
                if (on) WriteDword(root, path, val, onVal);
                else if (offVal < 0) DeleteValue(root, path, val);
                else WriteDword(root, path, val, offVal);
            };
            return it;
        }

        // 「键存在即开启」型（如 Win10 经典右键菜单）
        static SettingItem KeyOn(string cat, string name, string desc, string root, string path, bool admin, bool restart)
        {
            SettingItem it = new SettingItem();
            it.Category = cat;
            it.Name = name;
            it.Desc = desc;
            it.NeedAdmin = admin;
            it.NeedExplorerRestart = restart;
            it.RegPath = P(root, path, "");
            it.Probe = delegate { return Reg.Exists(root, path); };
            it.Set = delegate(bool on)
            {
                if (on)
                {
                    RegistryKey k = Reg.Hive(root).CreateSubKey(path, RegistryKeyPermissionCheck.Default);
                    if (k != null) k.Close();
                }
                else DeleteKeyTree(root, path);
            };
            return it;
        }

        // 命令型：probe / set 由外部给
        static SettingItem Cmd(string cat, string name, string desc, Func<bool> probe,
            Action<bool> set, bool admin, bool restart, string regPath, string risk = "")
        {
            SettingItem it = new SettingItem();
            it.Category = cat;
            it.Name = name;
            it.Desc = desc;
            it.NeedAdmin = admin;
            it.NeedExplorerRestart = restart;
            it.RegPath = regPath;
            it.Risk = risk;
            it.Probe = probe;
            it.Set = set;
            return it;
        }

        // 字符串值项：勾选写 onValue，取消删除该值
        static SettingItem Str(string cat, string name, string desc, string root, string path, string val,
            string onValue, bool admin, bool restart, string risk = "")
        {
            SettingItem it = new SettingItem();
            it.Category = cat;
            it.Name = name;
            it.Desc = desc;
            it.NeedAdmin = admin;
            it.NeedExplorerRestart = restart;
            it.Risk = risk;
            it.RegPath = P(root, path, val + " = \"" + onValue + "\"（默认无此值）");
            it.Probe = delegate
            {
                string v = ReadString(root, path, val);
                return v != null && string.Equals(v, onValue, StringComparison.OrdinalIgnoreCase);
            };
            it.Set = delegate(bool on)
            {
                if (on) WriteString(root, path, val, onValue);
                else DeleteValue(root, path, val);
            };
            return it;
        }

        // 组合项：一组值共用一个开关，全部满足才算「已开启」，勾选全写、取消全删
        static SettingItem Multi(string cat, string name, string desc, string regPath,
            (string Root, string Path, string Value, int On)[] values, bool admin, bool restart, string risk = "")
        {
            SettingItem it = new SettingItem();
            it.Category = cat;
            it.Name = name;
            it.Desc = desc;
            it.NeedAdmin = admin;
            it.NeedExplorerRestart = restart;
            it.Risk = risk;
            it.RegPath = regPath;
            it.Probe = delegate
            {
                foreach (var v in values)
                {
                    int? cur = ReadDword(v.Root, v.Path, v.Value);
                    if (!cur.HasValue || cur.Value != v.On) return false;
                }
                return true;
            };
            it.Set = delegate(bool on)
            {
                foreach (var v in values)
                {
                    if (on) WriteDword(v.Root, v.Path, v.Value, v.On);
                    else DeleteValue(v.Root, v.Path, v.Value);
                }
            };
            return it;
        }

        // ---------------- 桌面图标视图标志（FFlags） ----------------
        // FFlags 是「桌面」这个文件夹视图的一整块位标志，不是一个值一个开关。
        // 改单个开关必须读-改-写：整块覆盖会顺手把用户的排序方式、图标间距之类冲掉。
        const string BAGS = "Software\\Microsoft\\Windows\\Shell\\Bags\\1\\Desktop";
        const int FWF_AUTOARRANGE = 0x00000001;
        const int FWF_SNAPTOGRID = 0x00000004;

        /// <summary>FFlags 完全不存在时的基底值（桌面 + 搜索文件夹 + 全行选中 +
        /// 无客户区边框 + 自动排列 + 对齐网格）。只在值缺失时用，绝不覆盖已有值。</summary>
        const int FFLAGS_DEFAULT = 1075839525;

        /// <summary>只翻转 mask 指定的那一位，其余位原样保留。</summary>
        internal static int ApplyFlag(int current, int mask, bool on)
        {
            return on ? (current | mask) : (current & ~mask);
        }

        static SettingItem Flag(string cat, string name, string desc, string path, int mask, bool restart)
        {
            SettingItem it = new SettingItem();
            it.Category = cat;
            it.Name = name;
            it.Desc = desc;
            it.NeedAdmin = false;
            it.NeedExplorerRestart = restart;
            it.RegPath = P("HKCU", path, "FFlags 的位 0x" + mask.ToString("X"));
            it.Probe = delegate
            {
                int? v = ReadDword("HKCU", path, "FFlags");
                return v.HasValue && (v.Value & mask) != 0;
            };
            it.Set = delegate(bool on)
            {
                int? cur = ReadDword("HKCU", path, "FFlags");
                int v = cur.HasValue ? cur.Value : FFLAGS_DEFAULT;
                int want = ApplyFlag(v, mask, on);
                if (cur.HasValue && want == v) return;   // 没变化就别写，省得桌面无谓重绘
                WriteDword("HKCU", path, "FFlags", want);
            };
            return it;
        }

        // ---------------- 运行命令 ----------------
        static int RunCmd(string file, string args)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(file, args);
                si.CreateNoWindow = true;
                si.UseShellExecute = false;
                si.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(si);
                if (p == null) return -1;
                p.WaitForExit(60000);
                return p.ExitCode;
            }
            catch { return -1; }
        }

        static string RunPs(string script, int timeoutMs)
        {
            try
            {
                ProcessStartInfo si = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script.Replace("\"", "'") + "\"");
                si.CreateNoWindow = true;
                si.UseShellExecute = false;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                si.StandardOutputEncoding = Encoding.UTF8;
                Process p = Process.Start(si);
                if (p == null) return "";
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(timeoutMs);
                return outp == null ? "" : outp;
            }
            catch { return ""; }
        }

        // 服务启动类型（HKLM\SYSTEM\CurrentControlSet\Services\<名>\Start）
        static int? ServiceStart(string svc)
        {
            return ReadDword("HKLM", "SYSTEM\\CurrentControlSet\\Services\\" + svc, "Start");
        }

        // ---------------- 桌面图标显隐 ----------------
        /// <summary>
        /// 桌面图标显隐。值写在 <c>HKCU\...\HideDesktopIcons</c> 下的两个子键里，1 = 隐藏、0 = 显示。
        ///
        /// 老版本这 5 项写在 HKLM 下 —— 那是错的：系统只读 HKCU 那一份，
        /// 勾了等于没勾（而且还要管理员权限才写得进去）。
        ///
        /// 两个子键都要写：NewStartPanel 管 Win11 开始菜单式桌面，ClassicStartMenu 管
        /// 经典开始菜单式桌面。只写一个的话，在另一种桌面样式下会失效。
        /// </summary>
        static SettingItem DeskIcon(string name, string desc, string clsid)
        {
            string[] paths = new string[] {
                "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\NewStartPanel",
                "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\ClassicStartMenu"
            };

            SettingItem it = new SettingItem();
            it.Category = "桌面";
            it.Name = "在桌面显示「" + name + "」";
            it.Desc = desc;
            it.NeedAdmin = false;
            // 桌面图标列表是资源管理器启动时枚举的，改完值当场看不到变化，
            // 得重启资源管理器（页面上就有按钮）或按 F5 刷新桌面。标出来，别让人以为没生效。
            it.NeedExplorerRestart = true;
            it.RegPath = "HKCU\\...\\Explorer\\HideDesktopIcons\\{NewStartPanel, ClassicStartMenu} → "
                       + clsid + " = 0（1 = 隐藏）";
            it.Probe = delegate
            {
                // 两个子键里只要有一个还写着「隐藏」，就当作没显示出来
                foreach (string p in paths)
                    if (ReadDword("HKCU", p, clsid) == 1) return false;
                return true;
            };
            it.Set = delegate(bool on)
            {
                foreach (string p in paths)
                    WriteDword("HKCU", p, clsid, on ? 0 : 1);
                RefreshIcons();   // 桌面图标不会自己刷新，得主动通知 shell
            };
            return it;
        }

        // ---------------- 组装清单 ----------------
        public static List<SettingItem> Build()
        {
            List<SettingItem> L = new List<SettingItem>();
            const string ADV = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
            const string SI = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Icons";
            const string SEARCH = "Software\\Microsoft\\Windows\\CurrentVersion\\Search";
            const string NAMING = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\NamingTemplates";
            const string START = "Software\\Microsoft\\Windows\\CurrentVersion\\Start";
            // 下面几个是「用户策略」和 Win11 自己挪走的键，全在 HKCU 下，不需要管理员
            const string POL_EXPL = "Software\\Policies\\Microsoft\\Windows\\Explorer";
            const string PUSH = "Software\\Microsoft\\Windows\\CurrentVersion\\PushNotifications";
            const string NOTIF = "Software\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings";
            const string QUIET = "Software\\Microsoft\\Windows\\CurrentVersion\\QuietHours";
            const string CDM = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager";
            const string UPE = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\UserProfileEngagement";

            // ========== 一、资源管理器与文件夹 ==========
            L.Add(Dword("资源管理器", "显示文件扩展名", "文件名后面显示 .txt / .jpg 这类扩展名，改格式时更方便。",
                "HKCU", ADV, "HideFileExt", 0, 1, false, false));
            L.Add(Dword("资源管理器", "显示隐藏的文件和文件夹", "把设置了「隐藏」属性的项目显示出来。",
                "HKCU", ADV, "Hidden", 1, 0, false, false));
            L.Add(Dword("资源管理器", "显示受保护的操作系统文件", "连 desktop.ini 这类系统文件也显示（调试用）。",
                "HKCU", ADV, "ShowSuperHidden", 1, 0, false, false));
            L.Add(Dword("资源管理器", "标题栏显示完整路径", "资源管理器标题栏显示完整目录路径，方便复制。",
                "HKCU", ADV, "FullPath", 1, 0, false, false));
            L.Add(Dword("资源管理器", "快速访问显示最近使用的文件", "取消勾选可清掉「最近使用的文件」一栏。",
                "HKCU", ADV, "ShowRecent", 1, 0, false, false));
            L.Add(Dword("资源管理器", "快速访问显示常用文件夹", "取消勾选可清掉「常用文件夹」一栏。",
                "HKCU", ADV, "ShowFrequent", 1, 0, false, false));
            L.Add(Dword("资源管理器", "每个文件夹用独立窗口进程打开", "一个窗口崩溃不会连带其他窗口（略微多占内存）。",
                "HKCU", ADV, "SeparateProcess", 1, 0, false, false));
            L.Add(Dword("资源管理器", "默认打开「此电脑」而不是「快速访问」", "打开资源管理器直接进「此电脑」，跳过最近文件列表。",
                "HKCU", ADV, "LaunchTo", 1, -1, false, false));

            // ========== 二、右键菜单 ==========
            L.Add(KeyOn("右键菜单", "使用 Windows 10 经典右键菜单", "恢复「更多选项」那种完整右键菜单（Win11 默认是折叠的）。",
                "HKCU", "Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\\InprocServer32", false, true));
            L.Add(Cmd("右键菜单", "去掉快捷方式小箭头", "桌面快捷方式左下角不再显示小箭头（改图标实现，可随时恢复）。",
                delegate
                {
                    string v = ReadString("HKCU", SI, "29");
                    return v != null && v.Length > 0;
                },
                delegate(bool on)
                {
                    // 只动 29（箭头）。以前这里连 77（UAC 盾牌）一起写，
                    // 结果是勾了「去箭头」顺手把盾牌也去了 —— 两件事现在分开了。
                    if (on) WriteExpandString("HKCU", SI, "29", "%systemroot%\\system32\\imageres.dll,197");
                    else DeleteValue("HKCU", SI, "29");
                    RefreshIcons();
                }, false, true, "HKCU\\...\\Explorer\\Shell Icons → 29 = imageres.dll,197"));

            // 桌面图标显隐挪到「桌面」分类，并改写到 HKCU（原来写 HKLM，系统不读，等于没生效）

            // ========== 三、任务栏 ==========
            L.Add(Cmd("任务栏", "自动隐藏任务栏", "鼠标不在任务栏上时任务栏自动收起，多出一行屏幕空间。",
                delegate
                {
                    try
                    {
                        RegistryKey k = Registry.CurrentUser.OpenSubKey(
                            "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StuckRects3", false);
                        if (k == null) return false;
                        byte[] b = (byte[])k.GetValue("Settings");
                        k.Close();
                        return b != null && b.Length > 8 && b[8] == 3;   // 实测：3=自动隐藏，2=不隐藏
                    }
                    catch { return false; }
                },
                delegate(bool on)
                {
                    RegistryKey k = Registry.CurrentUser.OpenSubKey(
                        "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StuckRects3", true);
                    if (k == null) return;
                    try
                    {
                        byte[] b = (byte[])k.GetValue("Settings");
                        if (b != null && b.Length > 8)
                        {
                            b[8] = on ? (byte)3 : (byte)2;
                            k.SetValue("Settings", b, RegistryValueKind.Binary);
                        }
                    }
                    finally { k.Close(); }
                }, false, true, "HKCU\\...\\Explorer\\StuckRects3 → Settings[8] = 3 / 2（实测语义）"));

            L.Add(Dword("任务栏", "任务栏图标靠左", "Win11 默认居中，勾选后回到 Win10 那样靠左排列。",
                "HKCU", ADV, "TaskbarAl", 0, -1, false, true));
            L.Add(Multi("任务栏", "任务栏按钮从不合并", "像 Win10 那样每个窗口单独占一格、显示窗口标题，不再叠成一堆。",
                "HKCU\\...\\Explorer\\Advanced → TaskbarGlomLevel / MMTaskbarGlomLevel = 2（0 = 始终合并）",
                new (string Root, string Path, string Value, int On)[] {
                    ("HKCU", ADV, "TaskbarGlomLevel", 2),
                    ("HKCU", ADV, "MMTaskbarGlomLevel", 2),
                }, false, true));
            // 搜索框这一项要特判，两个原因：
            //
            // 1) Win11 把 CurrentVersion\Search 整个键锁死了，写入返回 0x80070005，
            //    管理员也不行（见 CanWriteKey 的注释）。能写的系统照常写；写不了的，
            //    勾选时别硬碰，直接把用户送到系统设置页。
            // 2) 不能用 Multi：Multi 是「两个值都满足才算已开启」，但用户在系统设置里
            //    手动关掉搜索框时，系统只写 SearchboxTaskbarMode，不写 ...Cache，
            //    那样工具箱会一直显示「默认状态」。所以探测只认主值。
            // 3) 写不进去也不能把用户推去系统设置 —— 改用整键还原通道（见 MutateProtectedKey），
            //    这条路实测能把值真正写进去。
            SettingItem hideSearch = Cmd("任务栏", "隐藏任务栏搜索框",
                "任务栏上不再显示搜索框；搜索本身还在，按 Win 键或 Win+S 照样能搜。",
                delegate
                {
                    int? v = ReadDword("HKCU", SEARCH, "SearchboxTaskbarMode");
                    return v.HasValue && v.Value == 0;
                },
                delegate(bool on)
                {
                    // 失败要真的报出来：整键还原可能在中途某一步失败（比如还原后补权限失败），
                    // 静默吞掉的话界面上只会显示「未生效」，排查时一点线索都没有
                    if (!SetTaskbarSearchMode(on ? 0 : 2, out string se))
                        throw new InvalidOperationException(se);
                }, false, false,
                "HKCU\\...\\CurrentVersion\\Search → SearchboxTaskbarMode = 0（0=隐藏 1=仅图标 2=搜索框）");

            if (!SearchKeyWritable())
                hideSearch.Desc = "任务栏上不再显示搜索框；搜索本身还在，按 Win 键或 Win+S 照样能搜。"
                                + "这个 Windows 版本锁住了搜索键的值写入，走「整键还原」通道改，慢一两秒，改完即时生效。";

            L.Add(hideSearch);
            L.Add(Dword("任务栏", "隐藏任务栏「聊天」按钮", "去掉任务栏上的 Teams 聊天入口。",
                "HKCU", ADV, "TaskbarMn", 0, -1, false, true));
            L.Add(Dword("任务栏", "隐藏托盘上的通知铃铛", "任务栏右下角不再显示通知铃铛图标。",
                "HKCU", ADV, "ShowNotificationIcon", 0, -1, false, true));

            L.Add(Dword("任务栏", "任务栏时钟显示秒", "时钟精确到秒（会略微多占一点资源，需重启资源管理器）。",
                "HKCU", ADV, "ShowSecondsInSystemClock", 1, 0, false, true));
            L.Add(Dword("任务栏", "显示任务视图按钮", "任务栏左侧显示「任务视图」（多桌面）按钮。",
                "HKCU", ADV, "ShowTaskViewButton", 1, 0, false, false));
            L.Add(Dword("任务栏", "显示 Copilot 按钮", "任务栏显示 Copilot（Windows AI）按钮。",
                "HKCU", ADV, "ShowCopilotButton", 1, 0, false, false));
            L.Add(Dword("任务栏", "关闭小组件（新闻和兴趣）", "任务栏左下角不再显示天气 / 新闻小组件。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Dsh", "AllowNewsAndInterests", 0, -1, true, false));
            L.Add(Dword("任务栏", "关闭锁屏动态内容（Windows 聚焦）", "锁屏不再轮播图片资讯，改为静态。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "EnableDynamicContentInWSB", 0, -1, true, false));

            // ========== 四、桌面 ==========
            L.Add(DeskIcon("此电脑", "在桌面上显示「此电脑」图标。", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
            L.Add(DeskIcon("回收站", "在桌面上显示「回收站」图标。", "{645FF040-5081-101B-9F08-00AA002F954E}"));
            L.Add(DeskIcon("用户的文件", "在桌面上显示当前用户的个人文件夹。", "{59031a47-3f72-44a7-89c5-5595fe6b30ee}"));
            L.Add(DeskIcon("网络", "在桌面上显示「网络」图标。", "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"));
            L.Add(DeskIcon("控制面板", "在桌面上显示「控制面板」图标。", "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}"));

            L.Add(Flag("桌面", "自动排列图标", "桌面图标按顺序自动排好，就不能再自由拖到任意位置了。",
                BAGS, FWF_AUTOARRANGE, true));
            L.Add(Flag("桌面", "图标对齐到网格", "拖动图标时自动吸附到网格线（一般配合「自动排列」一起用）。",
                BAGS, FWF_SNAPTOGRID, true));

            // 这里原本还想加「桌面图标按名称排序」，写 HKCU\...\Explorer\Advanced\Sort = Name。
            // 查不到任何可靠依据，而且 Bags\1\Desktop\Sort 是 REG_BINARY 的视图状态块，
            // 跟这个字符串值不是一回事 —— 大概率是「写了没人读」的安慰剂。
            // 右键菜单里点两下就能排序，不值得为它留一个可能永远返回「已开启」的假开关。

            L.Add(Cmd("桌面", "去掉 UAC 盾牌角标", "快捷方式右下角不再画蓝黄盾牌。只是不画图标，真正的提权提示照旧会弹。",
                delegate
                {
                    string v = ReadString("HKCU", SI, "77");
                    return v != null && v.Length > 0;
                },
                delegate(bool on)
                {
                    if (on) WriteExpandString("HKCU", SI, "77", "%systemroot%\\system32\\imageres.dll,197");
                    else DeleteValue("HKCU", SI, "77");
                    RefreshIcons();
                }, false, true, "HKCU\\...\\Explorer\\Shell Icons → 77 = imageres.dll,197"));

            L.Add(Str("桌面", "新建快捷方式不带「 - 快捷方式」后缀", "新建的快捷方式直接叫原名，不再自动加后缀。",
                "HKCU", NAMING, "ShortcutNameTemplate", "%s.lnk", false, false));

            // ========== 五、通知 ==========
            // 这四项系统里都散在好几个不同的键下，用 Multi 一次写全，
            // 免得出现「关了一半」的状态。
            L.Add(Multi("通知", "关闭通知中心", "任务栏的通知中心（Win+N）不再收集通知。",
                "HKCU\\...\\Policies\\Microsoft\\Windows\\Explorer → DisableNotificationCenter = 1",
                new (string Root, string Path, string Value, int On)[] {
                    ("HKCU", POL_EXPL, "DisableNotificationCenter", 1),
                    ("HKCU", PUSH, "ToastEnabled", 0),
                }, false, true));

            L.Add(Multi("通知", "关闭所有通知弹窗", "任何应用都不再在右下角弹横幅。",
                "HKCU\\...\\Notifications\\Settings → NOC_GLOBAL_SETTING_TOASTS_ENABLED = 0",
                new (string Root, string Path, string Value, int On)[] {
                    ("HKCU", NOTIF, "NOC_GLOBAL_SETTING_TOASTS_ENABLED", 0),
                    ("HKCU", PUSH, "NoCloudApplicationNotification", 1),
                }, false, false));

            L.Add(Dword("通知", "关闭专注助手（免打扰）", "不再按时段自动进入免打扰，通知该来就来。",
                "HKCU", QUIET, "QuietHoursEnabled", 0, -1, false, false));

            L.Add(Multi("通知", "关闭消费级建议 / 锁屏广告", "锁屏和开始菜单不再推广告、不偷偷装「推荐」应用、不弹软着陆引导。",
                "HKCU\\...\\ContentDeliveryManager（11 个值）+ UserProfileEngagement",
                new (string Root, string Path, string Value, int On)[] {
                    ("HKCU", CDM, "SubscribedContent-338387Enabled", 0),
                    ("HKCU", CDM, "SubscribedContent-338388Enabled", 0),
                    ("HKCU", CDM, "SubscribedContent-338389Enabled", 0),
                    ("HKCU", CDM, "SubscribedContent-353694Enabled", 0),
                    ("HKCU", CDM, "SubscribedContent-353696Enabled", 0),
                    ("HKCU", CDM, "SubscribedContent-310093Enabled", 0),
                    ("HKCU", CDM, "RotatingLockScreenEnabled", 0),
                    ("HKCU", CDM, "RotatingLockScreenOverlayEnabled", 0),
                    ("HKCU", CDM, "SoftLandingEnabled", 0),
                    ("HKCU", CDM, "SystemPaneSuggestionsEnabled", 0),
                    ("HKCU", CDM, "SilentInstalledAppsEnabled", 0),
                    ("HKCU", UPE, "ScoobeSystemSettingEnabled", 0),
                }, false, false));

            // ========== 六、开始菜单 ==========
            L.Add(Multi("开始菜单", "关闭开始菜单「推荐」区内容", "「推荐」区域不再显示最近添加的应用、常用应用和各类建议。",
                "HKCU\\...\\Explorer\\Advanced → Start_IrisRecommendations / Start_AccountNotifications = 0",
                new (string Root, string Path, string Value, int On)[] {
                    ("HKCU", ADV, "Start_IrisRecommendations", 0),
                    ("HKCU", ADV, "Start_AccountNotifications", 0),
                }, false, true));

            L.Add(Dword("开始菜单", "不显示最近打开的文件", "开始菜单、「文件资源管理器」的最新文件、跳转列表都不再记录文件。",
                "HKCU", ADV, "Start_TrackDocs", 0, -1, false, true));

            L.Add(Dword("开始菜单", "关闭「最近添加的应用」列表", "开始菜单不再列出最近装过的应用。",
                "HKCU", START, "ShowRecentList", 0, -1, false, true));

            // ========== 七、系统性能与系统服务 ==========
            L.Add(Cmd("系统性能", "关闭 SysMain 服务（原 Superfetch）", "减少内存占用与磁盘预读；机械硬盘用户不建议关。",
                delegate { int? s = ServiceStart("SysMain"); return s.HasValue && s.Value == 4; },
                delegate(bool on)
                {
                    RunCmd("sc", "config SysMain start= " + (on ? "disabled" : "auto"));
                    if (!on) RunCmd("sc", "start SysMain");
                }, true, false, "HKLM\\SYSTEM\\...\\Services\\SysMain\\Start = 4(禁用) / 2(自动)",
                "关闭后系统不再做内存预读和磁盘预加载。机械硬盘上开机会明显变慢，SSD 影响较小。确认关闭吗？"));
            L.Add(Cmd("系统性能", "关闭程序兼容性助手（PcaSvc）", "不再弹「程序兼容性助手」提示。",
                delegate { int? s = ServiceStart("PcaSvc"); return s.HasValue && s.Value == 4; },
                delegate(bool on)
                {
                    RunCmd("sc", "config PcaSvc start= " + (on ? "disabled" : "demand"));
                }, true, false, "HKLM\\SYSTEM\\...\\Services\\PcaSvc\\Start = 4(禁用) / 3(手动)"));
            L.Add(Cmd("系统性能", "关闭诊断策略服务（DPS）", "减少后台诊断活动，普通用户无感知。",
                delegate { int? s = ServiceStart("DPS"); return s.HasValue && s.Value == 4; },
                delegate(bool on)
                {
                    RunCmd("sc", "config DPS start= " + (on ? "disabled" : "demand"));
                }, true, false, "HKLM\\SYSTEM\\...\\Services\\DPS\\Start = 4(禁用) / 3(手动)"));
            L.Add(Dword("系统性能", "关闭客户体验改善计划", "不再向微软发送使用统计数据。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\SQMClient\\Windows", "CEIPEnable", 0, -1, true, false));
            L.Add(Dword("系统性能", "关闭系统还原", "不再创建还原点、释放硬盘空间（出问题无法滚回去，请谨慎）。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows NT\\SystemRestore", "DisableSR", 1, 0, true, false,
                "关闭后系统不再自动创建还原点。以后驱动装坏、更新出问题时，就没法用「系统还原」退回去了。确认关闭吗？"));
            L.Add(Cmd("系统性能", "关闭休眠（删除 hiberfil.sys）", "可省出几个 GB 空间；关闭后「快速启动」也随之失效。",
                delegate
                {
                    try
                    {
                        string root = Path.GetPathRoot(Environment.SystemDirectory);
                        return !File.Exists(Path.Combine(root, "hiberfil.sys"));
                    }
                    catch { return false; }
                },
                delegate(bool on)
                {
                    RunCmd("powercfg", "/h " + (on ? "off" : "on"));
                }, true, false, "powercfg /h on | off",
                "会删除系统盘根目录的 hiberfil.sys（通常几个 GB）。同时「快速启动」和「休眠」都会失效。确认关闭吗？"));
            L.Add(Dword("系统性能", "启用快速启动（混合启动）", "关机时把内核会话写入磁盘，下次开机更快。",
                "HKLM", "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power", "HiberbootEnabled", 1, 0, true, false));
            L.Add(Cmd("系统性能", "关闭内存压缩", "内存吃紧时不压缩内存页（内存充足时可减少 CPU 开销）。",
                delegate
                {
                    string o = RunPs("(Get-MMAgent).MemoryCompression", 25000).Trim();
                    return o.IndexOf("False", StringComparison.OrdinalIgnoreCase) >= 0;
                },
                delegate(bool on)
                {
                    RunPs(on ? "Disable-MMAgent -mc" : "Enable-MMAgent -mc", 25000);
                }, true, false, "MMAgent 内存压缩开关",
                "关掉后内存吃紧时系统不再压缩内存页，物理内存占用会上升。内存小于 16 GB 的机器不建议关。确认关闭吗？"));

            // ========== 八、Windows 更新 ==========
            L.Add(Cmd("Windows 更新", "暂停 Windows 更新（到 2042-07-07）", "功能更新和质量更新都暂停，随时可取消恢复。",
                delegate
                {
                    // 不能只看 FlightSettingsMaxPauseDays 在不在（系统自己也会写这个值），
                    // 要看「暂停到期时间」确实存在并且还没到。
                    byte[] raw = ReadBinary("HKLM", "SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings", "PauseUpdatesExpiryTime");
                    if (raw == null || raw.Length < 8) return false;
                    try
                    {
                        long ft = BitConverter.ToInt64(raw, 0);
                        return DateTime.FromFileTimeUtc(ft) > DateTime.UtcNow;
                    }
                    catch { return false; }
                },
                delegate(bool on)
                {
                    string p = "SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings";
                    if (on)
                    {
                        DateTime end = new DateTime(2042, 7, 7, 0, 0, 0, DateTimeKind.Utc);
                        byte[] start = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
                        byte[] b = BitConverter.GetBytes(end.ToFileTimeUtc());
                        // 开始时间必须是「现在」，和到期时间填同一个值 Windows 会判定无效
                        WriteBinary("HKLM", p, "PauseUpdatesStartTime", start);
                        WriteBinary("HKLM", p, "PauseUpdatesExpiryTime", b);
                        WriteBinary("HKLM", p, "PauseFeatureUpdatesStartTime", start);
                        WriteBinary("HKLM", p, "PauseFeatureUpdatesEndTime", b);
                        WriteBinary("HKLM", p, "PauseQualityUpdatesStartTime", start);
                        WriteBinary("HKLM", p, "PauseQualityUpdatesEndTime", b);
                        WriteDword("HKLM", p, "FlightSettingsMaxPauseDays", 10000);
                    }
                    else
                    {
                        DeleteValue("HKLM", p, "PauseUpdatesStartTime");
                        DeleteValue("HKLM", p, "PauseUpdatesExpiryTime");
                        DeleteValue("HKLM", p, "PauseFeatureUpdatesStartTime");
                        DeleteValue("HKLM", p, "PauseFeatureUpdatesEndTime");
                        DeleteValue("HKLM", p, "PauseQualityUpdatesStartTime");
                        DeleteValue("HKLM", p, "PauseQualityUpdatesEndTime");
                        DeleteValue("HKLM", p, "FlightSettingsMaxPauseDays");
                    }
                }, true, false, "HKLM\\SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings（6 个 FILETIME + 1 个 DWORD）",
                "会把功能更新和质量更新的暂停期限直接写到 2042 年，期间系统不会自动装任何更新（含安全补丁）。确认继续吗？"));
            L.Add(Dword("Windows 更新", "更新时排除驱动程序", "质量更新不再夹带驱动，避免显卡 / 声卡驱动被顶掉。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, 0, true, false));

            // ========== 九、隐私与搜索 ==========
            L.Add(Dword("隐私", "关闭广告 ID", "系统不再生成跨应用广告标识符。",
                "HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", 0, 1, false, false));
            L.Add(Cmd("隐私", "关闭输入个性化（键入与手写）", "不收集键入和手写数据用于改进输入法。",
                delegate
                {
                    return ReadDword("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\InputPersonalization", "RestrictImplicitTextCollection") == 1;
                },
                delegate(bool on)
                {
                    string p = "Software\\Microsoft\\Windows\\CurrentVersion\\InputPersonalization";
                    if (on)
                    {
                        WriteDword("HKCU", p, "RestrictImplicitTextCollection", 1);
                        WriteDword("HKCU", p, "RestrictImplicitInkCollection", 1);
                    }
                    else
                    {
                        DeleteValue("HKCU", p, "RestrictImplicitTextCollection");
                        DeleteValue("HKCU", p, "RestrictImplicitInkCollection");
                    }
                }, false, false, "HKCU\\...\\InputPersonalization"));
            L.Add(Dword("隐私", "不向网站提供本地语言列表", "浏览网页时不附带 Accept-Language 头信息。",
                "HKCU", "Control Panel\\International\\User Profile", "HttpAcceptLanguageOptOut", 1, 0, false, false));
            L.Add(Dword("隐私", "关闭开始菜单记录最近打开的程序", "开始菜单不再显示「最近添加」和「最近打开的项目」。",
                "HKCU", ADV, "Start_TrackProgs", 0, 1, false, false));
            L.Add(Cmd("隐私", "关闭活动历史记录", "不再记录本机使用记录、不上传、不同步到其他设备。",
                delegate
                {
                    return ReadDword("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\System", "EnableActivityFeed") == 0;
                },
                delegate(bool on)
                {
                    string p = "SOFTWARE\\Policies\\Microsoft\\Windows\\System";
                    if (on)
                    {
                        WriteDword("HKLM", p, "EnableActivityFeed", 0);
                        WriteDword("HKLM", p, "PublishUserActivities", 0);
                        WriteDword("HKLM", p, "UploadUserActivities", 0);
                    }
                    else
                    {
                        DeleteValue("HKLM", p, "EnableActivityFeed");
                        DeleteValue("HKLM", p, "PublishUserActivities");
                        DeleteValue("HKLM", p, "UploadUserActivities");
                    }
                }, true, false, "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\System"));
            L.Add(Cmd("隐私", "关闭 Windows 搜索联网（云内容 / Bing）", "搜索结果只出本地文件，不弹网页推荐。",
                delegate
                {
                    return ReadDword("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search", "AllowCloudSearch") == 0;
                },
                delegate(bool on)
                {
                    string p = "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search";
                    if (on)
                    {
                        WriteDword("HKLM", p, "AllowCloudSearch", 0);
                        WriteDword("HKLM", p, "BingSearchEnabled", 0);
                        WriteDword("HKLM", p, "ConnectedSearchUseWeb", 0);
                    }
                    else
                    {
                        DeleteValue("HKLM", p, "AllowCloudSearch");
                        DeleteValue("HKLM", p, "BingSearchEnabled");
                        DeleteValue("HKLM", p, "ConnectedSearchUseWeb");
                    }
                }, true, false, "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search"));
            L.Add(Dword("隐私", "关闭设备搜索历史", "搜索框不再保留本地设备搜索历史。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search\\SearchSettings", "IsDeviceSearchHistoryEnabled", 0, -1, true, false));
            L.Add(Dword("隐私", "关闭诊断遥测（设为最低）", "诊断数据只保留「必需」级别。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "AllowTelemetry", 0, 1, true, false));
            L.Add(Dword("隐私", "关闭反馈通知", "系统不再弹窗征求反馈。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "DoNotShowFeedbackNotifications", 1, 0, true, false));
            L.Add(Cmd("隐私", "阻止「搜索」应用联网（防火墙出站规则）", "给 SearchHost.exe 加一条出站阻止规则，比只关策略更彻底。",
                delegate
                {
                    string o = RunPs("(Get-NetFirewallRule -DisplayName '阻止Windows搜索联网' -ErrorAction SilentlyContinue).DisplayName", 30000);
                    return o.IndexOf("阻止Windows搜索联网", StringComparison.OrdinalIgnoreCase) >= 0;
                },
                delegate(bool on)
                {
                    if (on)
                    {
                        RunPs("New-NetFirewallRule -DisplayName '阻止Windows搜索联网' -Direction Outbound -Program $Env:SystemRoot\\SystemApps\\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\\SearchHost.exe -Action Block", 40000);
                    }
                    else
                    {
                        RunPs("Remove-NetFirewallRule -DisplayName '阻止Windows搜索联网'", 40000);
                    }
                }, true, false, "高级安全防火墙 – 出站规则",
                "会新增一条防火墙出站阻止规则。开始菜单搜索结果会变少（不含网络内容），取消勾选可删除该规则。确认继续吗？"));

            // ========== 十、对照「Windows 11 轻松设置」补齐的项 ==========
            //
            // 这一批是拿「Windows 11 轻松设置」V1.11 的开关清单逐条对过之后补的 ——
            // 它覆盖到、这里原来没有的那几项。取值全部在本机注册表上实测过，
            // 不是照抄它的实现：本项目的每一项都要有 Probe（能读回当前状态），
            // 而它的做法是「勾选时按反向标志位写死」，读不回来。
            L.Add(Dword("资源管理器", "隐藏没有插入媒体的驱动器", "「此电脑」里不再列出读卡器、空光驱这类没有介质的盘符。",
                "HKCU", ADV, "HideDrivesWithNoMedia", 1, -1, false, false));
            L.Add(Dword("资源管理器", "关闭缩略图预览（只显示图标）", "文件夹里不再为图片、视频生成缩略图，老机器上滚动更快。",
                "HKCU", ADV, "IconsOnly", 1, 0, false, false));
            L.Add(Dword("资源管理器", "快速访问不显示云文件", "OneDrive 之类的云盘文件不再出现在「快速访问」里。",
                "HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer", "ShowCloudFilesInQuickAccess", 0, 1, false, false));
            L.Add(Dword("资源管理器", "关闭 OneDrive「文件按需同步」集成", "资源管理器侧边栏不再挂 OneDrive 入口（不影响已装的 OneDrive 本体）。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\OneDrive", "DisableFileSyncNGSC", 1, -1, true, true));

            L.Add(Dword("任务栏", "隐藏任务栏小组件按钮", "去掉任务栏左下角的天气 / 小组件按钮（直接改任务栏值，不走策略）。",
                "HKCU", ADV, "TaskbarDa", 0, -1, false, true));

            L.Add(Multi("系统性能", "关闭远程协助与远程桌面", "别人无法远程连到这台电脑；本机照常可以远程连出去。",
                "HKLM\\SYSTEM\\...\\Control\\{Remote Assistance, Terminal Server} → fAllowToGetHelp = 0 / fDenyTSConnections = 1",
                new (string Root, string Path, string Value, int On)[] {
                    ("HKLM", "SYSTEM\\CurrentControlSet\\Control\\Remote Assistance", "fAllowToGetHelp", 0),
                    ("HKLM", "SYSTEM\\CurrentControlSet\\Control\\Terminal Server", "fDenyTSConnections", 1),
                }, true, false,
                "关闭后这台电脑不再接受远程协助邀请，也不再接受远程桌面连接。如果平时靠远程桌面操作这台机器，勾选后会连不上。确认继续吗？"));

            L.Add(Cmd("Windows 更新", "锁定当前大版本（不升到下一个版本）", "把功能更新钉在当前版本上，安全补丁和小版本更新照常安装。",
                delegate
                {
                    return ReadDword("HKLM", "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate", "TargetReleaseVersion") == 1;
                },
                delegate(bool on)
                {
                    string p = "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate";
                    if (on)
                    {
                        WriteDword("HKLM", p, "TargetReleaseVersion", 1);
                        // 值必须是「24H2」这种显示版本号，写错系统会忽略整条策略
                        string ver = ReadString("HKLM", "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", "DisplayVersion");
                        if (string.IsNullOrEmpty(ver)) ver = "24H2";
                        WriteString("HKLM", p, "TargetReleaseVersionInfo", ver);
                    }
                    else
                    {
                        DeleteValue("HKLM", p, "TargetReleaseVersion");
                        DeleteValue("HKLM", p, "TargetReleaseVersionInfo");
                    }
                }, true, false, "HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate → TargetReleaseVersion = 1 + TargetReleaseVersionInfo = 当前 DisplayVersion",
                "锁定后系统不会自动升级到下一个大版本（比如 24H2 → 25H2），直到你手动取消。安全更新不受影响。确认继续吗？"));

            L.Add(Dword("Windows 更新", "不推送恶意软件删除工具更新", "每月那条 KB890830 不再随 Windows 更新安装。",
                "HKLM", "SOFTWARE\\Policies\\Microsoft\\MRT", "DontOfferThroughWUAU", 1, -1, true, false));

            return L;
        }

        // ---------------- 一次性动作 ----------------
        /// <summary>
        /// 写入原语自检：在给定的临时键下把 Dword / String / 删除走一遍。
        ///
        /// 这几条是全部 65 个设置项的公共出口，只要它们是对的，
        /// 单个开关出问题就只剩「路径写错」一种可能，排查面小得多。
        /// </summary>
        /// <param name="path">临时键路径，调用方负责事后再删掉。</param>
        /// <param name="report">(是否通过, 说明)。</param>
        internal static void SelfTestRegistry(string path, Action<bool, string> report)
        {
            WriteDword("HKCU", path, "dw", 0);
            report(ReadDword("HKCU", path, "dw") == 0, "Dword 写 0 再读回");

            WriteDword("HKCU", path, "dw", -1);
            report(ReadDword("HKCU", path, "dw") == -1, "Dword 写 -1 能原样读回");

            DeleteValue("HKCU", path, "dw");
            report(ReadDword("HKCU", path, "dw") == null, "Dword 删除后读到 null");

            // 关键：%s.lnk 必须原样存下来。写成 REG_EXPAND_SZ 的话系统会试着展开，
            // 这个模板就废了 —— 而「新建快捷方式不带后缀」正是靠它。
            WriteString("HKCU", path, "sz", "%s.lnk");
            report(ReadString("HKCU", path, "sz") == "%s.lnk", "String 原样存回 \"%s.lnk\"");

            RegistryValueKind? kind = null;
            try
            {
                RegistryKey k = Reg.Open("HKCU", path, false);
                if (k != null)
                {
                    try { kind = k.GetValueKind("sz"); }
                    finally { k.Close(); }
                }
            }
            catch { }
            report(kind == RegistryValueKind.String, "String 写的是 REG_SZ（不是 REG_EXPAND_SZ）");

            DeleteValue("HKCU", path, "sz");
            report(ReadString("HKCU", path, "sz") == null, "String 删除后读到 null");
        }

        /// <summary>
        /// 整键还原通道自检：在一个临时键上跑一遍
        /// 存盘 → 挂载 → 改副本 → 卸载 → RegRestoreKey，确认值真能写进去、
        /// 而且跑完键的权限跟原来一模一样（RegSaveKey 不存安全描述符，这步最容易漏）。
        /// </summary>
        internal static void SelfTestMutate(string path, Action<bool, string> report)
        {
            if (!RightMenuActions.IsAdmin())
            {
                report(true, "整键还原通道（跳过：需要管理员，RegSaveKey 要 SeBackupPrivilege）");
                return;
            }

            WriteDword("HKCU", path, "probe", 1);
            string aclBefore = AclSignature("HKCU", path);

            bool ok = MutateProtectedKey("HKCU", path,
                k => k.SetValue("probe", 9, RegistryValueKind.DWord), out string err);
            report(ok, "整键还原能写进去" + (ok ? "" : "：" + err));
            if (!ok) return;

            int? v = ReadDword("HKCU", path, "probe");
            report(v.HasValue && v.Value == 9, "整键还原后读回 9（实际 " + Show(v) + "）");

            // 不能直接比 SDDL 字符串：还原之后系统会把继承 ACE 重新铺一遍，
            // 多出一个 AI 标记和几条 inherit-only 的条目 —— 那是系统自己的动作，
            // 有效权限没变。所以只比「真正生效的那几条 ACE」。
            string aclAfter = AclSignature("HKCU", path);
            report(aclAfter == aclBefore, "整键还原后键权限没被改掉"
                + (aclAfter == aclBefore ? "" : "（前 " + aclBefore + " / 后 " + aclAfter + "）"));
        }

        /// <summary>
        /// 键的「有效权限签名」：只取直接写在键上、且真正生效的 ACE，
        /// 排序后拼成字符串。用来比对权限有没有被动过，避开继承条目和 AI 标记的干扰。
        /// </summary>
        internal static string AclSignature(string root, string path)
        {
            try
            {
                using (RegistryKey k = Reg.Hive(root).OpenSubKey(path, false))
                {
                    if (k == null) return null;
                    RegistrySecurity sec = k.GetAccessControl(AccessControlSections.Access);
                    // 用集合：还原之后同一个 ACE 会同时以「显式」和「继承」两份出现，
                    // 权限本身没变，只是来源变了，不该算作改动。
                    HashSet<string> parts = new HashSet<string>(StringComparer.Ordinal);
                    // 显式的和继承来的都要算 —— 两者对「能不能访问」效果一样，
                    // 只有 inherit-only 的条目（OICIID 那种）对键本身不起作用，排除掉。
                    foreach (AuthorizationRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    {
                        RegistryAccessRule rr = r as RegistryAccessRule;
                        if (rr == null) continue;
                        if (rr.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                        parts.Add((rr.AccessControlType == AccessControlType.Allow ? "A" : "D")
                            + "|" + rr.IdentityReference.Value + "|" + rr.RegistryRights);
                    }
                    List<string> sorted = new List<string>(parts);
                    sorted.Sort(StringComparer.Ordinal);
                    return string.Join("；", sorted);
                }
            }
            catch { return null; }
        }

        public static void EmptyRecycleBin()
        {
            try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); }
            catch { }
        }

        public static void RestartExplorer()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); } catch { }
                }
                System.Threading.Thread.Sleep(600);
                RunCmd("explorer.exe", "");
            }
            catch { }
        }

        public static void RefreshIcons()
        {
            try { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int RegDeleteKeyW(IntPtr hKey, string subKey);

        /// <summary>
        /// 踢一下这个键，让资源管理器重新读一遍。
        ///
        /// <para>
        /// 整键还原把外壳挂在这个键上的 <c>RegNotifyChangeKeyValue</c> 弄断了，改完它不知道。
        /// 广播 <c>WM_SETTINGCHANGE</c> 试过，**只能让任务栏把搜索框删掉、建不回来**，
        /// 而且两次广播实测要花掉 4 秒多。真正管用的是注册表变更通知 ——
        /// 而我们唯一还能做的写操作就是建子键，所以：建一个临时子键 → 通知发出 → 立刻删掉。
        /// </para>
        /// <para>
        /// 子键建的时候给一个「当前用户完全控制」的描述符，但**这个键照样删不掉**
        /// （写保护和 DACL 无关），所以它会在下次调用 <see cref="MutateProtectedKey"/> 时
        /// 被 <see cref="SweepTempSubKeys"/> 随整键还原一起清掉。任何时刻最多留一个空子键。
        /// </para>
        /// </summary>
        static void KickShell(string root, string path)
        {
            string name = "__wbrefresh__";
            try
            {
                RegistrySecurity sec = new RegistrySecurity();
                sec.AddAccessRule(new RegistryAccessRule(CurrentUser(), RegistryRights.FullControl,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

                using (RegistryKey k = Reg.Hive(root).CreateSubKey(path + "\\" + name,
                    RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryOptions.None, sec))
                {
                    if (k != null) k.SetValue("kick", 1, RegistryValueKind.DWord);
                }
            }
            catch { }

            try
            {
                IntPtr hRoot = string.Equals(root, "HKLM", StringComparison.OrdinalIgnoreCase)
                    ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER;
                RegDeleteKeyW(hRoot, path + "\\" + name);
            }
            catch { }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, int dwFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern void SHChangeNotify(long wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        // 快捷入口（名称, 目标）
        public static string[][] QuickLinks()
        {
            return new string[][] {
                new string[] { "Windows 设置", "ms-settings:" },
                new string[] { "隐私和安全性", "ms-settings:privacy" },
                new string[] { "Windows 更新", "ms-settings:windowsupdate" },
                new string[] { "系统属性", "sysdm.cpl" },
                new string[] { "文件夹选项", "shell32.dll,Options_RunDLL 0" },
                new string[] { "电源选项", "powercfg.cpl" },
                new string[] { "网络连接", "ncpa.cpl" },
                new string[] { "程序和功能", "appwiz.cpl" },
                new string[] { "服务", "services.msc" },
                new string[] { "任务计划程序", "taskschd.msc" }
            };
        }

        // 打开快捷入口（rundll32 那条要拆开 file/args）
        public static void OpenLink(string target)
        {
            try
            {
                if (target.StartsWith("shell32.dll,", StringComparison.OrdinalIgnoreCase))
                {
                    RunCmd("rundll32.exe", target);
                    return;
                }
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LastError = "打不开：" + ex.Message;
            }
        }
    }

}
