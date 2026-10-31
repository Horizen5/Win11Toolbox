// ================================================================
//  RightMenuActions.cs —— 右键菜单管理 · 动作层
//  从独立版的 MenuPanel 里抽出来的"真正干活"的部分：
//  备份并禁用、恢复已屏蔽/已禁用、单独导出、从 .reg 还原、重启资源管理器、提权。
//  全部无 UI 依赖，失败原因通过 log 回调抛给页面。
// ================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

#nullable disable

namespace Win11Toolbox.RightMenu
{
    internal sealed class ActionReport
    {
        public int Ok;
        public int Fail;
        public int Skipped;
        public string BackupDir = "";
        public readonly List<string> Lines = new List<string>();

        public string Summary
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("成功 ").Append(Ok).Append(" 项");
                if (Fail > 0) sb.Append("，失败 ").Append(Fail).Append(" 项");
                if (Skipped > 0) sb.Append("，跳过 ").Append(Skipped).Append(" 项");
                return sb.ToString();
            }
        }
    }

    internal static class RightMenuActions
    {
        public static bool IsAdmin()
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void Log(Action<string> log, string msg)
        {
            log?.Invoke(msg);
        }

        public static string MakeSafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "backup";
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                bool ok = true;
                for (int i = 0; i < bad.Length; i++) if (c == bad[i]) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            string r = sb.ToString().Trim();
            if (r.Length > 60) r = r.Substring(0, 60).Trim();
            return r.Length == 0 ? "backup" : r;
        }

        // ---------------------------------------------------------------- 备份并禁用
        /// <summary>备份到 backups\时间戳\，然后禁用。返回报告。</summary>
        public static ActionReport Apply(List<RightMenuItem> sel, Action<string> log)
        {
            var rep = new ActionReport();
            if (sel == null || sel.Count == 0) return rep;

            var keysToDelete = new List<string>();
            var blockedToAdd = new List<string>();
            var keyItems = new List<RightMenuItem>();

            foreach (var mi in sel)
            {
                if (mi.IsBlocked || mi.IsHistory) continue;
                if (mi.Kind == ItemKind.ShellEx) blockedToAdd.Add(mi.Clsid);
                else { keysToDelete.Add(mi.FullPath); keyItems.Add(mi); }
            }

            if (keysToDelete.Count == 0 && blockedToAdd.Count == 0)
                return rep;

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine(AppPaths.BackupRoot, stamp);
            rep.BackupDir = dir;
            Directory.CreateDirectory(dir);

            // README
            var readme = new StringBuilder();
            readme.Append("Win11 工具箱 · 右键菜单备份\r\n");
            readme.Append("时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n\r\n");
            readme.Append("被禁用的项目：\r\n");
            foreach (var mi in sel)
                readme.Append("  · [").Append(mi.Category).Append("] ").Append(mi.DisplayName)
                      .Append("   =>  ").Append(mi.FullPath).Append("\r\n");
            readme.Append("\r\n还原方法：在「右键菜单」页点「从备份文件恢复…」，选本目录的 restore.reg。\r\n");
            try { File.WriteAllText(Path.Combine(dir, "README.txt"), readme.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Log(log, "写 README 失败：" + ex.Message); }

            // restore.reg
            string regFile = Path.Combine(dir, "restore.reg");
            try { RegFile.Export(regFile, keysToDelete); }
            catch (Exception ex) { Log(log, "导出总备份失败：" + ex.Message); }

            // 逐项备份 + 禁用记录
            var newEntries = new List<DisabledLog.Entry>();
            int seq = 0;
            foreach (var mi in keyItems)
            {
                seq++;
                string fn = seq.ToString("00") + "_" + MakeSafeName(mi.DisplayName) + ".reg";
                string itemDir = Path.Combine(dir, "items");
                string itemReg = Path.Combine(itemDir, fn);
                try
                {
                    if (!Directory.Exists(itemDir)) Directory.CreateDirectory(itemDir);
                    RegFile.Export(itemReg, new List<string> { mi.FullPath });
                }
                catch (Exception ex) { Log(log, "单项备份失败 " + mi.DisplayName + "：" + ex.Message); }

                var en = new DisabledLog.Entry
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Category = mi.Category,
                    Name = mi.DisplayName,
                    Root = mi.Root,
                    Path = mi.Path,
                    Kind = mi.Kind.ToString(),
                    Clsid = mi.Clsid,
                    IconSpec = mi.IconSpec,
                    AppName = mi.AppName,
                    TargetFile = mi.TargetFile,
                    ItemReg = Path.Combine(Path.Combine("backups", stamp), Path.Combine("items", fn)),
                };
                newEntries.Add(en);
            }
            try { DisabledLog.Append(AppPaths.ExeDir, newEntries); }
            catch (Exception ex) { Log(log, "写禁用记录失败：" + ex.Message); }

            // 屏蔽项补进 restore.reg（还原 = 删掉这些值）
            if (blockedToAdd.Count > 0)
            {
                var extra = new StringBuilder();
                extra.Append("\r\n[HKEY_LOCAL_MACHINE\\").Append(Scanner.BlockedPath).Append("]\r\n");
                foreach (string c in blockedToAdd) extra.Append("\"").Append(c).Append("\"=-\r\n");
                extra.Append("\r\n[HKEY_CURRENT_USER\\").Append(Scanner.BlockedPath).Append("]\r\n");
                foreach (string c in blockedToAdd) extra.Append("\"").Append(c).Append("\"=-\r\n");
                try { File.AppendAllText(regFile, extra.ToString(), new UnicodeEncoding(false, false)); }
                catch (Exception ex) { Log(log, "写屏蔽还原段失败：" + ex.Message); }
            }

            // 执行：删键
            foreach (string kp in keysToDelete)
            {
                int i = kp.IndexOf('\\');
                if (i <= 0) { rep.Fail++; continue; }
                string root = kp.Substring(0, i);
                string path = kp.Substring(i + 1);
                try
                {
                    Reg.Hive(root).DeleteSubKeyTree(path, false);
                    rep.Ok++;
                }
                catch (Exception ex) { rep.Fail++; Log(log, "删除失败 " + kp + "：" + ex.Message); }
            }

            // 执行：写屏蔽列表
            foreach (string c in blockedToAdd)
            {
                try
                {
                    using (var h = Registry.LocalMachine.CreateSubKey(Scanner.BlockedPath))
                        h.SetValue(c, "Blocked by Win11 Toolbox", RegistryValueKind.String);
                    rep.Ok++;
                }
                catch
                {
                    try
                    {
                        using (var h = Registry.CurrentUser.CreateSubKey(Scanner.BlockedPath))
                            h.SetValue(c, "Blocked by Win11 Toolbox", RegistryValueKind.String);
                        rep.Ok++;
                    }
                    catch (Exception ex2) { rep.Fail++; Log(log, "屏蔽失败 " + c + "：" + ex2.Message); }
                }
            }

            Log(log, "已禁用 " + rep.Ok + " 项，失败 " + rep.Fail + " 项。备份：" + dir);
            return rep;
        }

        // ---------------------------------------------------------------- 恢复
        public static ActionReport Restore(List<RightMenuItem> sel, Action<string> log)
        {
            var rep = new ActionReport();
            if (sel == null || sel.Count == 0) return rep;

            var blocked = new List<RightMenuItem>();
            var history = new List<RightMenuItem>();
            foreach (var mi in sel)
            {
                if (mi.IsBlocked) blocked.Add(mi);
                else if (mi.IsHistory) history.Add(mi);
            }
            if (blocked.Count == 0 && history.Count == 0) return rep;

            foreach (var mi in blocked)
            {
                foreach (string root in new[] { "HKLM", "HKCU" })
                {
                    using var k = Reg.Open(root, Scanner.BlockedPath, true);
                    if (k == null) continue;
                    try { k.DeleteValue(mi.Clsid, false); rep.Ok++; }
                    catch (Exception ex) { rep.Fail++; Log(log, "解除失败 " + mi.Clsid + "：" + ex.Message); }
                }
                Log(log, "已解除屏蔽：" + mi.DisplayName);
            }

            foreach (var mi in history)
                if (RestoreOneHistory(mi, false, log)) rep.Ok++;
                else rep.Fail++;

            Log(log, "恢复完成：" + rep.Summary);
            return rep;
        }

        /// <summary>用单项备份把一项「已禁用」的项目写回注册表。</summary>
        public static bool RestoreOneHistory(RightMenuItem mi, bool complain, Action<string> log)
        {
            if (mi == null || !mi.IsHistory) return false;

            string full = AppPaths.ResolveBackupPath(mi.BackupItemReg);
            if (full.Length == 0 || !File.Exists(full))
            {
                if (complain)
                    Log(log, "找不到这一项的单项备份：" + (mi.BackupItemReg.Length > 0 ? mi.BackupItemReg : "(未记录)"));
                else
                    Log(log, "跳过（找不到单项备份）：" + mi.DisplayName);
                return false;
            }

            var errors = new List<string>();
            int n = 0;
            try { n = RegFile.Import(full, errors); }
            catch (Exception ex) { Log(log, "还原失败 " + mi.DisplayName + "：" + ex.Message); }

            if (n > 0)
            {
                Log(log, "已恢复「" + mi.DisplayName + "」（" + n + " 处改动）");
                try { DisabledLog.Remove(AppPaths.ExeDir, mi.Root, mi.Path); } catch { }
                return true;
            }
            if (errors.Count > 0) Log(log, "还原 " + mi.DisplayName + " 出错：" + errors[0]);
            return false;
        }

        /// <summary>把一项单独导出成 .reg（外壳扩展导出的是「解除屏蔽」语句）。</summary>
        public static void ExportOne(RightMenuItem mi, string file)
        {
            if (mi == null || string.IsNullOrEmpty(file)) return;
            if (mi.Kind == ItemKind.ShellEx)
            {
                var sb = new StringBuilder();
                sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
                sb.Append("[HKEY_LOCAL_MACHINE\\").Append(Scanner.BlockedPath).Append("]\r\n\"")
                  .Append(mi.Clsid).Append("\"=-\r\n\r\n");
                sb.Append("[HKEY_CURRENT_USER\\").Append(Scanner.BlockedPath).Append("]\r\n\"")
                  .Append(mi.Clsid).Append("\"=-\r\n");
                File.WriteAllText(file, sb.ToString(), new UnicodeEncoding(false, false));
            }
            else
            {
                RegFile.Export(file, new List<string> { mi.FullPath });
            }
        }

        /// <summary>导入任意 .reg 备份。</summary>
        public static int RestoreFromFile(string file, out int fail, Action<string> log)
        {
            fail = 0;
            var errors = new List<string>();
            int n = 0;
            try { n = RegFile.Import(file, errors); }
            catch (Exception ex) { Log(log, "导入失败：" + ex.Message); }
            fail = errors.Count;
            foreach (string e in errors) Log(log, "  ! " + e);
            Log(log, "已应用 " + n + " 处改动，失败 " + fail + " 处。");
            return n;
        }

        // ---------------------------------------------------------------- 系统动作
        public static void RestartExplorer()
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { }
            }
            System.Threading.Thread.Sleep(1200);
            try { Process.Start("explorer.exe"); } catch { }
        }

        public static void OpenFileLocation(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            Process.Start("explorer.exe", "/select,\"" + file + "\"");
        }

        public static void OpenDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        public static void OpenInRegedit(string fullRegPath)
        {
            Process.Start("regedit.exe", "/m \"Computer\\" + fullRegPath + "\"");
        }

        /// <summary>以管理员身份重启自己。</summary>
        public static bool Elevate()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exe.Length == 0) return false;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas",
                });
                return true;
            }
            catch { return false; }
        }

        /// <summary>这一项关联的真实文件（右键「打开文件所在位置」用）。</summary>
        public static string FileOf(RightMenuItem mi)
        {
            if (mi == null) return "";
            if (!string.IsNullOrEmpty(mi.TargetFile))
            {
                try { if (File.Exists(mi.TargetFile)) return mi.TargetFile; } catch { }
            }
            try { return Scanner.FileOfSpec(mi.IconSpec); }
            catch { return ""; }
        }

        public static string KindText(ItemKind k)
        {
            if (k == ItemKind.ShellKey) return "传统菜单项（注册表键）";
            if (k == ItemKind.ShellEx) return "外壳扩展（COM 组件）";
            return "命名空间图标（此电脑 / 桌面）";
        }
    }
}
