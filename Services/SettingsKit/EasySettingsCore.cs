// ================================================================
//  EasySettingsCore.cs —— 系统轻松设置 · 逻辑层
//  由「右键菜单管家 v3.0」的轻松设置模块移植，去掉 WinForms。
//  40 项安全设置，每项都带 Probe（当前状态）/ Set（应用或恢复默认）。
// ================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

        static void WriteDword(string root, string path, string name, int val)
        {
            RegistryKey k = Reg.Hive(root).CreateSubKey(path, RegistryKeyPermissionCheck.Default);
            if (k == null) throw new InvalidOperationException("打不开 " + root + "\\" + path);
            try { k.SetValue(name, val, RegistryValueKind.DWord); }
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

        // ---------------- 组装清单 ----------------
        public static List<SettingItem> Build()
        {
            List<SettingItem> L = new List<SettingItem>();
            const string ADV = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
            const string SI = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Icons";

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

            // ========== 二、右键菜单与桌面图标 ==========
            L.Add(KeyOn("右键菜单", "使用 Windows 10 经典右键菜单", "恢复「更多选项」那种完整右键菜单（Win11 默认是折叠的）。",
                "HKCU", "Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\\InprocServer32", false, true));
            L.Add(Cmd("右键菜单", "去掉快捷方式小箭头", "桌面快捷方式左下角不再显示小箭头（改图标实现，可随时恢复）。",
                delegate
                {
                    try
                    {
                        RegistryKey k = Registry.CurrentUser.OpenSubKey(SI, false);
                        if (k == null) return false;
                        object v = k.GetValue("29", null);
                        k.Close();
                        return v != null;
                    }
                    catch { return false; }
                },
                delegate(bool on)
                {
                    if (on)
                    {
                        WriteExpandString("HKCU", SI, "29", "%systemroot%\\system32\\imageres.dll,197");
                        WriteExpandString("HKCU", SI, "77", "%systemroot%\\system32\\imageres.dll,197");
                    }
                    else
                    {
                        DeleteValue("HKCU", SI, "29");
                        DeleteValue("HKCU", SI, "77");
                    }
                }, false, true, "HKCU\\...\\Explorer\\Shell Icons → 29 / 77 = imageres.dll,197"));

            string[] deskIcons = new string[] {
                "{20D04FE0-3AEA-1069-A2D8-08002B30309D}|此电脑",
                "{645FF040-5081-101B-9F08-00AA002F954E}|回收站",
                "{59031a47-3f72-44a7-89c5-5595fe6b30ee}|用户的文件",
                "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}|网络",
                "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}|控制面板"
            };
            string HDK = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\HideDesktopIcons\\NewStartPanel";
            foreach (string di in deskIcons)
            {
                string[] parts = di.Split('|');
                string clsid = parts[0], cn = parts[1];
                L.Add(Dword("右键菜单", "在桌面显示「" + cn + "」", "Win11 默认隐藏部分桌面图标，勾选后把它显示出来。",
                    "HKLM", HDK, clsid, 0, 1, true, false));
            }

            // ========== 三、任务栏与开始菜单 ==========
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

            // ========== 四、系统性能与系统服务 ==========
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

            // ========== 五、Windows 更新 ==========
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

            // ========== 六、隐私与搜索 ==========
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

            return L;
        }

        // ---------------- 一次性动作 ----------------
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
