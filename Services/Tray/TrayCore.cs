// ================================================================
//  TrayCore.cs —— 托盘图标管理 · 逻辑层
//  由「右键菜单管家 v3.0」的托盘模块移植，去掉 WinForms：
//    · 设置页「其他系统托盘图标」清单（UIA 读开关）
//    · 托盘实况枚举（任务栏可见区 + 溢出区）
//    · 两个来源按名字打分合并
// ================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using Microsoft.Win32;
using Win11Toolbox.RightMenu;

#nullable disable

namespace Win11Toolbox.Tray
{
    internal class TrayIcon
    {
        public string AppName = "";        // 显示名：设置条目名优先，其次悬停原文
        public string SettingsName = "";   // 清单里的显示名（空 = 不在清单里）
        public string SettingsKey = "";    // 注册表子键名（空 = 不在清单里），写回时用它
        public int SettingsState = -1;     // -1=未知 0=关（在溢出区） 1=开（始终显示）
        public string ExePath = "";        // 归属进程路径（尽力而为）
        public string Tooltip = "";        // 托盘悬停原文
        public string Where = "";          // 任务栏 / 溢出区 / 未运行
        public bool Live;                  // 当前是否真的在托盘里
        public ImageSource Icon;
        public string Key                  // 稳定标识
        {
            get { return (SettingsName.Length > 0 ? SettingsName : AppName).ToLowerInvariant(); }
        }
    }

    // ---------------- 原生 API（只要窗口枚举那几个） ----------------
    internal static class TrayNative
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr proc, IntPtr baseAddr, byte[] buf, int size, out int read);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr proc, int flags, StringBuilder buf, ref int size);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_VM_READ = 0x0010;
    }
    // ---------------- 托盘图标清单：直接读写注册表 ----------------
    //  Win11 把「其他系统托盘图标」那份清单存在
    //      HKCU\Control Panel\NotifyIconSettings\<哈希子键>
    //        ExecutablePath  REG_SZ    归属程序（可能以 {GUID}\ 开头，指某个已知文件夹）
    //        InitialTooltip  REG_SZ    悬停文字（经常是空的）
    //        IsPromoted      REG_DWORD 1=始终显示在任务栏；缺失或 0=收进溢出区
    //  系统设置页本身就是读写这里，所以不必开设置页、不必 UIA，
    //  改完 explorer 会立刻跟着变，也就不会弹出任何窗口。
    internal static class TraySettings
    {
        private const string RootPath = @"Control Panel\NotifyIconSettings";

        public class Entry
        {
            public string Key = "";        // 注册表子键名（写回时用它定位，稳定且唯一）
            public string Name = "";       // 显示名
            public string ExePath = "";    // 归属程序（已把 {GUID} 前缀解析成真实路径）
            public bool On;                // true=始终显示 false=在溢出区
        }

        public static List<Entry> ReadEntries(StringBuilder log)
        {
            List<Entry> list = new List<Entry>();
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(RootPath))
                {
                    if (root == null)
                    {
                        if (log != null) log.AppendLine("本机没有 NotifyIconSettings 键（Win11 22H2 之前没有这份清单），只能看托盘实况");
                        return list;
                    }
                    string[] subs = root.GetSubKeyNames();
                    foreach (string sub in subs)
                    {
                        try
                        {
                            using (RegistryKey k = root.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string exe = k.GetValue("ExecutablePath") as string;
                                string tip = k.GetValue("InitialTooltip") as string;
                                object promo = k.GetValue("IsPromoted");

                                Entry en = new Entry();
                                en.Key = sub;
                                en.ExePath = ResolvePath(exe ?? "");
                                en.On = promo is int n && n != 0;
                                en.Name = DisplayName(tip, en.ExePath);
                                list.Add(en);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null) log.AppendLine("读托盘注册表失败：" + ex.Message);
            }
            list.Sort(delegate (Entry a, Entry b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        // items 的 Key = 注册表子键名，Value = 想要的状态（true 始终显示）
        public static string Apply(List<KeyValuePair<string, bool>> items, StringBuilder log)
        {
            if (items.Count == 0) return "没有要切换的项目";
            int done = 0, skipped = 0;
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(RootPath, true))
                {
                    if (root == null)
                    {
                        if (log != null) log.AppendLine("打不开 NotifyIconSettings（当前用户没有写权限）");
                        return "打不开托盘图标注册表";
                    }
                    foreach (KeyValuePair<string, bool> kv in items)
                    {
                        RegistryKey k = null;
                        try
                        {
                            k = root.OpenSubKey(kv.Key, true);
                            if (k == null)
                            {
                                if (log != null) log.AppendLine("  × " + kv.Key + "：注册表里找不到这一项");
                                skipped++;
                                continue;
                            }
                            object promo = k.GetValue("IsPromoted");
                            bool cur = promo is int n && n != 0;
                            string name = DisplayName(k.GetValue("InitialTooltip") as string,
                                                      ResolvePath((k.GetValue("ExecutablePath") as string) ?? ""));
                            if (cur == kv.Value)
                            {
                                if (log != null) log.AppendLine("  - " + name + "：已经是目标状态");
                                done++;
                                continue;
                            }
                            k.SetValue("IsPromoted", kv.Value ? 1 : 0, RegistryValueKind.DWord);
                            if (log != null)
                                log.AppendLine("  √ " + name + " → " + (kv.Value ? "始终显示在任务栏" : "收进溢出区"));
                            done++;
                        }
                        catch (Exception ex)
                        {
                            if (log != null) log.AppendLine("  × " + kv.Key + " 写入失败：" + ex.Message);
                            skipped++;
                        }
                        finally { if (k != null) k.Close(); }
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null) log.AppendLine("写入失败：" + ex.Message);
                return "写入失败：" + ex.Message;
            }

            // 通知 shell 重新读一遍（系统设置页改完也是这样刷新的）
            try
            {
                IntPtr r = SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero,
                                              "TraySettings", 0x0002, 1000, out IntPtr _);
            }
            catch { }

            return "完成 " + done + " 项" + (skipped > 0 ? "，跳过 " + skipped + " 项" : "");
        }

        // ---------- 内部 ----------

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
                                                        string lParam, uint flags, uint timeout, out IntPtr result);

        // 注册表里的路径可能是 {6D809377-...}\ToDesk\ToDesk.exe，{GUID} 是已知文件夹
        private static string ResolvePath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw[0] != '{') return raw;
            int end = raw.IndexOf('}');
            if (end <= 1) return raw;
            Guid g;
            if (!Guid.TryParse(raw.Substring(1, end - 1), out g)) return raw;
            IntPtr p = IntPtr.Zero;
            try
            {
                if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p) != 0) return raw;
                string rootDir = Marshal.PtrToStringUni(p) ?? "";
                string rest = raw.Substring(end + 1).TrimStart('\\');
                if (rootDir.Length == 0) return raw;
                return rest.Length == 0 ? rootDir : Path.Combine(rootDir, rest);
            }
            catch { return raw; }
            finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
        }

        // 悬停文字优先；空的话退回程序文件名（很多条目 InitialTooltip 是空的）
        private static string DisplayName(string tip, string exe)
        {
            string t = (tip ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length > 0) return t;
            try
            {
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    // exe 里的「文件说明」通常是产品名，比 Weixin.exe 这种文件名好认
                    string desc = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
                }
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(exe))
                {
                    string n = Path.GetFileNameWithoutExtension(exe);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            return "未知图标";
        }
    }

    // ---------------- 合并：设置清单 + 托盘实况 ----------------
    internal static class TrayCore
    {
        // 读全部数据：注册表清单 + 进程运行状态，合并成一行行
        public static List<TrayIcon> Load(StringBuilder log)
        {
            List<TrayIcon> rows = new List<TrayIcon>();

            // ① 托盘图标清单（注册表，权威）
            List<TraySettings.Entry> entries = TraySettings.ReadEntries(log);
            if (log != null) log.AppendLine("托盘图标清单读到 " + entries.Count + " 项（注册表 NotifyIconSettings）");

            // ② 正在运行的进程。按 exe 全路径索引，比去枚举托盘按钮稳得多，
            //    也不需要点开溢出区、不会弹出任何界面。
            HashSet<string> runPaths, runNames;
            RunningSet(out runPaths, out runNames);
            if (log != null) log.AppendLine("当前运行的程序 " + runPaths.Count + " 个");

            // ③ 每条清单条目 → 一行
            int liveCount = 0;
            foreach (TraySettings.Entry en in entries)
            {
                TrayIcon ti = new TrayIcon();
                ti.AppName = en.Name;
                ti.SettingsName = en.Name;
                ti.SettingsKey = en.Key;
                ti.SettingsState = en.On ? 1 : 0;
                ti.ExePath = en.ExePath;

                bool live = false;
                if (en.ExePath.Length > 0)
                {
                    live = runPaths.Contains(en.ExePath);
                    if (!live)
                    {
                        try { live = runNames.Contains(Path.GetFileNameWithoutExtension(en.ExePath)); }
                        catch { }
                    }
                }
                ti.Live = live;
                ti.Where = live ? (en.On ? "任务栏" : "溢出区") : "未运行";
                if (live) liveCount++;
                rows.Add(ti);
            }
            if (log != null) log.AppendLine("其中正在运行 " + liveCount + " 个");

            // ④ 补图标
            foreach (TrayIcon ti in rows)
            {
                try
                {
                    if (ti.ExePath.Length > 0 && File.Exists(ti.ExePath))
                        ti.Icon = IconLib.FromExe(ti.ExePath);
                }
                catch { }
            }
            return rows;
        }

        // 当前运行的进程：全路径集合 + 进程名集合（有些提权进程拿不到路径）
        private static void RunningSet(out HashSet<string> paths, out HashSet<string> names)
        {
            paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] all = Process.GetProcesses();
            foreach (Process p in all)
            {
                try
                {
                    names.Add(p.ProcessName);
                    string f = null;
                    try { f = p.MainModule.FileName; } catch { }
                    if (!string.IsNullOrEmpty(f)) paths.Add(f);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // 常见「中文名 ↔ 英文名」别名：设置页常用英文名，托盘悬停常用中文名
        // （例如设置页写 Weixin，托盘悬停写「微信」——不看别名就配不上对）
        static readonly string[][] Aliases = new string[][] {
            new string[] { "微信", "weixin" },
            new string[] { "企业微信", "wxwork" },
            new string[] { "腾讯会议", "wemeet" },
            new string[] { "腾讯视频", "qqlive" },
            new string[] { "qq音乐", "qqmusic" },
            new string[] { "网易云音乐", "cloudmusic" },
            new string[] { "酷狗音乐", "kugou" },
            new string[] { "酷我音乐", "kwmusic" },
            new string[] { "迅雷", "thunder" },
            new string[] { "百度网盘", "baidunetdisk" },
            new string[] { "百度输入法", "baiduime" },
            new string[] { "阿里云盘", "aliyunpan" },
            new string[] { "夸克", "quark" },
            new string[] { "向日葵", "awesun" },
            new string[] { "钉钉", "dingtalk" },
            new string[] { "飞书", "feishu" },
            new string[] { "抖音", "douyin" },
            new string[] { "快手", "kuaishou" },
            new string[] { "哔哩哔哩", "bilibili" },
            new string[] { "爱奇艺", "iqiyi" },
            new string[] { "优酷", "youku" },
            new string[] { "芒果tv", "mgtv" },
            new string[] { "搜狗输入法", "sogou" },
            new string[] { "网易uu加速器", "uu" },
            new string[] { "火绒安全", "huorong" },
            new string[] { "腾讯电脑管家", "qqpcmgr" },
            new string[] { "360安全卫士", "360safe" }
        };

        // 名字相似度：供"设置条目 ↔ 托盘悬停"配对
        public static int NameScore(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            string na = Norm(a), nb = Norm(b);
            if (na.Length == 0 || nb.Length == 0) return 0;
            int score = 0;
            if (na == nb) score += 100;
            else if (na.Contains(nb) || nb.Contains(na)) score += 60;
            // 公共前缀
            int p = 0;
            while (p < na.Length && p < nb.Length && na[p] == nb[p]) p++;
            if (p >= 4) score += p;
            // 分词重叠
            string[] ta = Tokens(a), tb = Tokens(b);
            int shared = 0;
            foreach (string x in ta)
                foreach (string y in tb)
                    if (x.Length >= 2 && x == y) { shared++; break; }
            score += shared * 15;
            // 别名加成：Weixin ↔ 微信 这种中英文名
            if (score < 60)
            {
                string la = a.ToLowerInvariant(), lb = b.ToLowerInvariant();
                foreach (string[] al in Aliases)
                {
                    if (al[0].Trim().Length == 0) continue;
                    if ((la.IndexOf(al[1], StringComparison.OrdinalIgnoreCase) >= 0
                            && lb.IndexOf(al[0], StringComparison.OrdinalIgnoreCase) >= 0)
                        || (la.IndexOf(al[0], StringComparison.OrdinalIgnoreCase) >= 0
                            && lb.IndexOf(al[1], StringComparison.OrdinalIgnoreCase) >= 0))
                    { score += 90; break; }
                }
            }
            return score;
        }

        private static string Norm(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string[] Tokens(string s)
        {
            List<string> list = new List<string>();
            StringBuilder cur = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) cur.Append(char.ToLowerInvariant(c));
                else { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Length = 0; } }
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list.ToArray();
        }

        // 名字 → 进程路径（按文件名/产品名/文件描述/进程名打分）
        public static string MatchProcess(string appName)
        {
            if (string.IsNullOrEmpty(appName)) return "";
            // 中文名 ↔ 英文名别名一起拿去匹配（"微信" → Weixin.exe）
            List<string> names = new List<string>();
            names.Add(appName);
            foreach (string[] al in Aliases)
            {
                if (appName.IndexOf(al[0], StringComparison.OrdinalIgnoreCase) >= 0
                    && appName.IndexOf(al[1], StringComparison.OrdinalIgnoreCase) < 0)
                    names.Add(appName.Replace(al[0], al[1]));
                if (appName.IndexOf(al[1], StringComparison.OrdinalIgnoreCase) >= 0
                    && appName.IndexOf(al[0], StringComparison.OrdinalIgnoreCase) < 0)
                    names.Add(appName.Replace(al[1], al[0]));
            }
            string best = "";
            int bestScore = 0;
            foreach (string nm in names)
            {
                string p = MatchProcessOne(nm);
                if (p.Length > 0) return p;     // 任意一个写法命中就采用
            }
            return "";
        }

        private static string MatchProcessOne(string appName)
        {
            string norm = Norm(appName);
            if (norm.Length < 2) return "";
            string[] want = Tokens(appName);
            string best = "";
            int bestScore = 0;
            foreach (Process p in Process.GetProcesses())
            {
                string path = "";
                try { path = p.MainModule == null ? "" : p.MainModule.FileName; }
                catch { continue; }
                if (path.Length == 0) continue;
                int score = 0;
                string fn = Path.GetFileNameWithoutExtension(path);
                string nfn = Norm(fn);
                if (nfn == norm) score += 50;
                else if (nfn.Length >= 3 && (nfn.Contains(norm) || norm.Contains(nfn))) score += 30;
                string pn = Norm(p.ProcessName);
                if (pn == norm) score += 20;
                else if (pn.Length >= 3 && (pn.Contains(norm) || norm.Contains(pn))) score += 10;
                try
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
                    string prod = Norm(vi.ProductName ?? "");
                    string desc = Norm(vi.FileDescription ?? "");
                    if (prod == norm) score += 40;
                    else if (prod.Length >= 3 && (prod.Contains(norm) || norm.Contains(prod))) score += 25;
                    if (desc == norm) score += 40;
                    else if (desc.Length >= 3 && (desc.Contains(norm) || norm.Contains(desc))) score += 25;
                }
                catch { }
                // 分词重叠（处理 "AMD Software: Host Application" ↔ "AMD Software: Adrenalin Edition"）
                string[] have = Tokens(fn + " " + p.ProcessName);
                int shared = 0;
                foreach (string x in want)
                {
                    if (x.Length < 2) continue;
                    foreach (string y in have)
                        if (x == y) { shared++; break; }
                }
                score += shared * 10;
                if (score > bestScore) { bestScore = score; best = path; }
            }
            return bestScore >= 30 ? best : "";
        }

        // 进程路径 → 给人看的名字
        public static string Describe(string exePath)
        {
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrEmpty(vi.ProductName)) return vi.ProductName;
                if (!string.IsNullOrEmpty(vi.FileDescription)) return vi.FileDescription;
            }
            catch { }
            try
            {
                string fn = Path.GetFileNameWithoutExtension(exePath);
                if (!string.IsNullOrEmpty(fn)) return fn;
            }
            catch { }
            return "";
        }
    }

}
