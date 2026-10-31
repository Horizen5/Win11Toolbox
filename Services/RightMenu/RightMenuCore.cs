// ================================================================
//  RightMenuCore.cs —— 右键菜单管理 · 逻辑层
//  由独立版「右键菜单管家 v3.0」移植而来，去掉全部 WinForms 依赖：
//    · 注册表读写 / .reg 导出导入 / 扫描器 / 禁用记录 / MUI 字符串解析
//    · 图标提取改为 HICON -> ImageSource（WPF 可直接绑定）
//  界面在 Pages/RightMenuPage.xaml
// ================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

#nullable disable

namespace Win11Toolbox.RightMenu
{
    /// <summary>程序目录 / 备份目录（原来是 WinForms 的 Application.StartupPath）。</summary>
    internal static class AppPaths
    {
        public static string ExeDir
        {
            get { return AppContext.BaseDirectory; }
        }

        public static string BackupRoot
        {
            get { return Path.Combine(ExeDir, "backups"); }
        }

        public static string ResolveBackupPath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return "";
            try
            {
                if (Path.IsPathRooted(rel)) return rel;
                return Path.Combine(ExeDir, rel);
            }
            catch { return ""; }
        }
    }

    internal enum ItemKind
    {
        ShellKey,   // 传统菜单项（一个 subkey）
        ShellEx,    // COM 外壳扩展（用 Blocked 列表屏蔽）
        NameSpace   // 此电脑 / 桌面 里的命名空间图标
    }

    internal class RightMenuItem
    {
        public bool Checked;
        public string Category;
        public string DisplayName;
        public string Root;
        public string Path;
        public ItemKind Kind;
        public string Clsid;
        public bool IsSystem;
        public bool IsBlocked;
        public string Detail;
        public string IconSpec;     // 归属软件的图标来源，如 "C:\...\app.exe,0" 或 "imageres.dll,-109"
        public string AppName;      // 归属软件名（用于"按软件筛选"下拉框）
        public string TargetFile;   // 这一项关联的真实文件（右键"打开文件所在位置"用）
        public string MenuHint;     // 外壳扩展在右键菜单里可能显示的文字（v2.3）
        public List<string> MenuNames = new List<string>();   // 上面那条的完整候选列表

        // ---- 禁用历史（v2.3）：这一条是从日志里读出来的"曾经被我禁用过"的记录 ----
        public bool IsHistory;
        public string DisabledAt = "";
        public string BackupItemReg = "";   // 单项备份 .reg 的路径（相对 exe 目录）

        public RightMenuItem()
        {
            Checked = false;
            Category = "";
            DisplayName = "";
            Root = "";
            Path = "";
            Clsid = "";
            Detail = "";
            IconSpec = "";
            AppName = "";
            TargetFile = "";
        }

        // 完整注册表路径，如 HKEY_LOCAL_MACHINE\SOFTWARE\...
        public string FullRegPath
        {
            get { return Reg.RootName(Root) + "\\" + Path; }
        }

        public string FullPath
        {
            get { return Root + "\\" + Path; }
        }

        public string StatusText
        {
            get
            {
                if (IsHistory) return "已禁用";
                if (IsBlocked) return "已屏蔽";
                if (IsSystem) return "系统自带";
                return "正常";
            }
        }

        // 给用户看的"人话"名称：外壳扩展优先用它在右键菜单里的文字
        public string Title
        {
            get
            {
                if (!string.IsNullOrEmpty(MenuHint)) return MenuHint;
                return DisplayName;
            }
        }
    }

    // ======================= 图标提取 =======================
    //  从 "C:\..\xxx.dll,-109" 这类规格里取出真实图标。
    //  三级兜底：ExtractIconEx（正索引） -> ExtractIcon（支持负号=资源 ID）
    //           -> LoadLibraryEx + LoadImage（最后防线）
    // ======================= 图标提取（WPF 版） =======================
    //  从 "C:\..\xxx.dll,-109" 这类规格里取出真实图标，返回可直接绑定的 ImageSource。
    //  三级兜底：ExtractIconEx（正索引） -> ExtractIcon（支持负号=资源 ID）
    //           -> LoadLibraryEx + LoadImage（最后防线）
    internal static class IconLib
    {
        private const uint IMAGE_ICON = 1;
        private const uint LR_DEFAULTSIZE = 0x00000040;
        private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

        private const uint SHGFI_ICON = 0x00000100;
        private const uint SHGFI_SMALLICON = 0x00000001;
        private const uint SHGFI_LARGEICON = 0x00000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;

        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
        private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconW")]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string file, int index);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO fi, uint cb, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW", SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr hFile, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadImageW")]
        private static extern IntPtr LoadImage(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static void Destroy(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return;
            try { DestroyIcon(hIcon); } catch { }
        }

        // HICON -> 冻结的 ImageSource（拷完立刻销毁原句柄，避免泄漏）
        private static ImageSource GrabAndDestroy(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return null;
            ImageSource result = null;
            try
            {
                BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                result = src;
            }
            catch { result = null; }
            Destroy(hIcon);
            return result;
        }

        // 把 "imageres.dll" / "%windir%\x.dll" / "C:\..\x.dll" 解析成真实存在的完整路径
        public static string ResolveFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return "";
            string s = file.Trim();
            if (s.Length > 0 && s[0] == '@') s = s.Substring(1).Trim();
            s = s.Trim('"').Trim();
            if (s.Length == 0) return "";
            try { s = Environment.ExpandEnvironmentVariables(s); }
            catch { }
            if (s.IndexOf('\\') >= 0)
            {
                try { return File.Exists(s) ? s : ""; }
                catch { return ""; }
            }
            string win = Environment.GetEnvironmentVariable("windir");
            if (string.IsNullOrEmpty(win)) win = "C:\\Windows";
            string[] probes = new string[] {
                win + "\\System32\\" + s,
                win + "\\SysWOW64\\" + s,
                win + "\\" + s
            };
            foreach (string p in probes)
            {
                try { if (File.Exists(p)) return p; }
                catch { }
            }
            return "";
        }

        // 这些 DLL 里的图标是"系统通用图标"，代表不了第三方软件本身
        private static readonly string[] SystemIconDlls = new string[] {
            "imageres.dll", "shell32.dll", "pifmgr.dll", "moricons.dll", "urlmon.dll"
        };

        public static bool IsSystemIconSpec(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return false;
            string file = Scanner.StripIndex(spec);
            if (file.Length == 0) return false;
            string resolved = ResolveFile(file);
            if (resolved.Length == 0) resolved = file;
            string name;
            try { name = Path.GetFileName(resolved).ToLowerInvariant(); }
            catch { return false; }
            if (name.Length == 0) return false;
            foreach (string d in SystemIconDlls) if (name == d) return true;
            return false;
        }

        // 用 shell 自己对这个 exe 的文件关联图标
        public static ImageSource FromExe(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (!File.Exists(path)) return null;
                SHFILEINFO fi = new SHFILEINFO();
                uint cb = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
                IntPtr r = SHGetFileInfo(path, 0, ref fi, cb, SHGFI_ICON | SHGFI_LARGEICON);
                if (r == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return null;
                return GrabAndDestroy(fi.hIcon);
            }
            catch { return null; }
        }

        private static bool IsSystemDir(string dir)
        {
            string d = (dir == null) ? "" : dir.ToLowerInvariant().TrimEnd('\\');
            if (d.Length == 0) return true;
            string win = Environment.GetEnvironmentVariable("windir");
            if (string.IsNullOrEmpty(win)) win = "C:\\Windows";
            win = win.ToLowerInvariant().TrimEnd('\\');
            return (d == win) || d.StartsWith(win + "\\");
        }

        private static bool IsRootMarker(string dir)
        {
            string d = (dir == null) ? "" : dir.ToLowerInvariant().TrimEnd('\\');
            if (d.Length <= 2) return true;
            string[] marks = new string[] {
                "\\program files", "\\program files (x86)", "\\programdata",
                "\\appdata\\local", "\\appdata\\roaming", "\\appdata", "\\users",
                "\\windows", "\\system32", "\\syswow64"
            };
            foreach (string m in marks)
                if (d.EndsWith(m)) return true;
            return false;
        }

        private static readonly string[] SkipExeMarks = new string[] {
            "uninstall", "unins", "update", "upgrade", "setup", "install",
            "repair", "crashdump", "redist", "vcredist", "bootstrapper", "stub"
        };

        private static bool IsSkippableExe(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (string m in SkipExeMarks) if (n.IndexOf(m) >= 0) return true;
            return false;
        }

        private static bool IsHostExe(string name)
        {
            string n = name.ToLowerInvariant();
            return n == "rundll32.exe" || n == "cmd.exe" || n == "mshta.exe" ||
                   n == "cscript.exe" || n == "wscript.exe" || n == "conhost.exe" ||
                   n == "regsvr32.exe" || n == "powershell.exe" || n == "pwsh.exe" ||
                   n == "explorer.exe";
        }

        private static void VersionOf(string file, out string company, out string product)
        {
            company = ""; product = "";
            try
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(file);
                company = (fvi.CompanyName ?? "").Trim();
                product = (fvi.ProductName ?? "").Trim();
            }
            catch { }
        }

        public static ImageSource FindNearbyAppIcon(string file)
        {
            string dir;
            try { dir = Path.GetDirectoryName(file); }
            catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;

            string srcCompany = "", srcProduct = "";
            try { VersionOf(file, out srcCompany, out srcProduct); } catch { }

            int guard = 0;
            while (!string.IsNullOrEmpty(dir) && guard++ < 4)
            {
                if (IsSystemDir(dir) || IsRootMarker(dir)) return null;
                try
                {
                    string[] exes = Directory.GetFiles(dir, "*.exe");
                    if (exes.Length > 0 && exes.Length <= 400)
                    {
                        List<KeyValuePair<int, string>> cand = new List<KeyValuePair<int, string>>();
                        foreach (string e in exes)
                        {
                            string fn = "";
                            try { fn = Path.GetFileName(e); } catch { }
                            if (fn.Length == 0) continue;
                            if (IsHostExe(fn)) continue;
                            int score = 0;
                            if (!IsSkippableExe(fn)) score += 2;
                            string co = "", pr = "";
                            try { VersionOf(e, out co, out pr); } catch { }
                            if (srcCompany.Length > 0 && co.Length > 0 &&
                                string.Equals(co, srcCompany, StringComparison.OrdinalIgnoreCase))
                                score += 3;
                            if (srcProduct.Length > 0 && pr.Length > 0 &&
                                (pr.IndexOf(srcProduct, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 srcProduct.IndexOf(pr, StringComparison.OrdinalIgnoreCase) >= 0))
                                score += 2;
                            long len = 0;
                            try { len = new FileInfo(e).Length; } catch { }
                            if (len > 1024 * 1024) score += 1;
                            cand.Add(new KeyValuePair<int, string>(score, e));
                        }
                        cand.Sort(delegate(KeyValuePair<int, string> a, KeyValuePair<int, string> b)
                        {
                            int c = b.Key.CompareTo(a.Key);
                            return (c != 0) ? c : string.CompareOrdinal(b.Value, a.Value);
                        });
                        int tried = 0;
                        foreach (KeyValuePair<int, string> kv in cand)
                        {
                            if (tried++ >= 5) break;
                            ImageSource ic = FromFileIndex(kv.Value, 0);
                            if (ic != null) return ic;
                            ic = FromExe(kv.Value);
                            if (ic != null) return ic;
                        }
                    }
                }
                catch { }
                try { dir = Path.GetDirectoryName(dir); }
                catch { break; }
            }
            return null;
        }

        public static ImageSource FromSpec(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            string s = spec.Trim();
            if (s.Length == 0) return null;

            int index = 0;
            string file = s;
            int comma = s.LastIndexOf(',');
            if (comma > 0)
            {
                string tail = s.Substring(comma + 1).Trim();
                int v;
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                {
                    index = v;
                    file = s.Substring(0, comma);
                }
            }
            file = ResolveFile(file);
            if (file.Length == 0) return null;

            return FromFileIndex(file, index);
        }

        public static ImageSource FromFileIndex(string file, int index)
        {
            if (index >= 0)
            {
                IntPtr hl = IntPtr.Zero, hs = IntPtr.Zero;
                uint n = 0;
                try { n = ExtractIconEx(file, index, out hl, out hs, 1); }
                catch { }
                if (n > 0 && (hl != IntPtr.Zero || hs != IntPtr.Zero))
                {
                    IntPtr use = hl != IntPtr.Zero ? hl : hs;
                    IntPtr other = hl != IntPtr.Zero ? hs : hl;
                    ImageSource ic = GrabAndDestroy(use);
                    if (ic == null && other != IntPtr.Zero)
                    {
                        ic = GrabAndDestroy(other);
                        other = IntPtr.Zero;
                    }
                    Destroy(other);
                    if (ic != null) return ic;
                }
                else
                {
                    Destroy(hl);
                    Destroy(hs);
                }
            }

            try
            {
                IntPtr h = ExtractIcon(IntPtr.Zero, file, index);
                if (h != IntPtr.Zero && h != (IntPtr)1)
                {
                    ImageSource ic = GrabAndDestroy(h);
                    if (ic != null) return ic;
                }
            }
            catch { }

            try
            {
                IntPtr mod = LoadLibraryEx(file, IntPtr.Zero, LOAD_LIBRARY_AS_IMAGE_RESOURCE);
                if (mod != IntPtr.Zero)
                {
                    IntPtr name = new IntPtr(index & 0xFFFF);
                    IntPtr h = LoadImage(mod, name, IMAGE_ICON, 32, 32, 0);
                    if (h == IntPtr.Zero) h = LoadImage(mod, name, IMAGE_ICON, 0, 0, LR_DEFAULTSIZE);
                    ImageSource ic = (h != IntPtr.Zero) ? GrabAndDestroy(h) : null;
                    FreeLibrary(mod);
                    if (ic != null) return ic;
                }
            }
            catch { }

            return null;
        }

        public static ImageSource ShellStock(string probePath, uint attrs)
        {
            try
            {
                SHFILEINFO fi = new SHFILEINFO();
                uint cb = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
                IntPtr r = SHGetFileInfo(probePath, attrs, ref fi, cb,
                                         SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                if (r == IntPtr.Zero || fi.hIcon == IntPtr.Zero) return null;
                return GrabAndDestroy(fi.hIcon);
            }
            catch { return null; }
        }
    }


    // ======================= 外壳扩展的"菜单文字"（v2.3） =======================
    //  外壳扩展（ContextMenuHandlers）在右键菜单里显示什么文字，注册表里通常查不到
    //  —— 那是扩展运行时自己决定的。但绝大多数扩展会把菜单文字放在自己的
    //  STRINGTABLE 资源里（例如 WPS 的 "上传或同步到WPS"），这里就把它们读出来。
    //
    //  重要：用 LOAD_LIBRARY_AS_DATAFILE 打开 = 只把文件当数据映射进来，
    //  不会执行 DLL 里的任何代码，也不会加载它的依赖，安全。
    internal static class DllText
    {
        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
        private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;
        private const int RT_STRING = 6;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr hResData);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

        private static readonly Dictionary<string, List<string>> _cache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // 常见繁体字：含这些字的多半是繁体版本，跳过（同一条文字一般有简体版）
        private const string TraditionalChars =
            "網盤刪屬訊設權選進開關檔傳統這為與個內從們裏當應鏈接語體類別編輯檢視圖" +
            "標籤頁碼專區員頭像資產數據雲端絡載佈滿東車馬門問間無發對課題寫讀認識語言";

        public static List<string> Candidates(string file)
        {
            if (string.IsNullOrEmpty(file)) return new List<string>();
            List<string> cached;
            if (_cache.TryGetValue(file, out cached)) return cached;

            List<string> result = new List<string>();
            try { result = Extract(file); }
            catch { }
            _cache[file] = result;
            return result;
        }

        private static List<string> Extract(string file)
        {
            List<string> raw = new List<string>();
            if (!File.Exists(file)) return new List<string>();

            IntPtr h = LoadLibraryEx(file, IntPtr.Zero,
                                     LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
            if (h == IntPtr.Zero) return new List<string>();
            try
            {
                List<uint> blocks = new List<uint>();
                EnumResNameProc cb = delegate(IntPtr m, IntPtr t, IntPtr name, IntPtr p)
                {
                    long id = name.ToInt64();
                    if (id > 0 && id < 0x10000) blocks.Add((uint)id);
                    return true;
                };
                EnumResourceNames(h, (IntPtr)RT_STRING, cb, IntPtr.Zero);
                GC.KeepAlive(cb);

                foreach (uint b in blocks)
                {
                    IntPtr info = FindResource(h, (IntPtr)b, (IntPtr)RT_STRING);
                    if (info == IntPtr.Zero) continue;
                    IntPtr res = LoadResource(h, info);
                    uint size = SizeofResource(h, info);
                    IntPtr data = (res == IntPtr.Zero) ? IntPtr.Zero : LockResource(res);
                    if (data == IntPtr.Zero || size < 2) continue;

                    int off = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        if (off + 2 > (int)size) break;
                        int len = (ushort)Marshal.ReadInt16(data, off);
                        off += 2;
                        if (len <= 0) continue;
                        if (off + len * 2 > (int)size) break;
                        string s = Marshal.PtrToStringUni(new IntPtr(data.ToInt64() + off), len);
                        off += len * 2;
                        if (s != null) raw.Add(s);
                    }
                }
            }
            finally { FreeLibrary(h); }

            // ---- 过滤 + 挑选 ----
            List<string> ok = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (string s0 in raw)
            {
                string s = (s0 == null) ? "" : s0.Trim();
                if (s.Length < 2 || s.Length > 20) continue;
                if (s.IndexOf("$(") >= 0) continue;        // 带占位符的模板串
                if (!HasCjk(s)) continue;                  // 只要含中文的
                if (HasKana(s)) continue;                  // 日文
                if (HasTraditional(s)) continue;           // 繁体（一般有对应简体）
                if (seen.Contains(s)) continue;
                seen.Add(s);
                ok.Add(s);
            }

            // 如果全都是 2-4 字的短名词，多半是列表列名（"名称 / 大小 / 类型 / 修改时间"），
            // 不是右键菜单文字，宁可不显示
            if (ok.Count >= 4)
            {
                bool allShort = true;
                foreach (string s in ok) if (s.Length > 4) { allShort = false; break; }
                if (allShort) return new List<string>();
            }

            // 两三个字的泛词（"下载""属性""刷新"）信息量太低，容易误导；
            // 只有存在更具体的短语时才展示
            List<string> good = new List<string>();
            foreach (string s in ok) if (s.Length >= 4) good.Add(s);
            if (good.Count == 0) return new List<string>();

            if (good.Count > 8) good = good.GetRange(0, 8);   // 保留资源里的原始顺序
            return good;
        }

        private static bool HasCjk(string s)
        {
            foreach (char c in s) if (c >= 0x4E00 && c <= 0x9FFF) return true;
            return false;
        }

        private static bool HasKana(string s)
        {
            foreach (char c in s) if (c >= 0x3040 && c <= 0x30FF) return true;
            return false;
        }

        private static bool HasTraditional(string s)
        {
            foreach (char c in s) if (TraditionalChars.IndexOf(c) >= 0) return true;
            return false;
        }

        // 拼成给列表看的一行，如 "上传或同步到WPS / 上传到WPS / 我的设备"
        public static string Join(List<string> names, int max)
        {
            if (names == null || names.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Count && i < max; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(names[i]);
            }
            return sb.ToString();
        }
    }

    // ======================= 禁用记录（v2.3） =======================
    //  传统菜单项是靠"直接删注册表键"禁用的，删完它就从扫描结果里消失了 ——
    //  结果就是：过两天再打开软件，列表里干干净净，谁也看不出"这里原本还有一项"，
    //  别人拿到这份软件/备份更是一头雾水。
    //  所以每次禁用都往 exe 同目录的 disabled.tsv 里记一笔：重新扫描时，
    //  这些项仍然以「已禁用」的样子留在列表里，随时能一键恢复。
    internal static class DisabledLog
    {
        public const string FileName = "disabled.tsv";

        public class Entry
        {
            public string Time = "";
            public string Category = "";
            public string Name = "";
            public string Root = "";
            public string Path = "";
            public string Kind = "";
            public string Clsid = "";
            public string IconSpec = "";
            public string AppName = "";
            public string TargetFile = "";
            public string ItemReg = "";
        }

        private static string PathOf(string exeDir)
        {
            return System.IO.Path.Combine(exeDir, FileName);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }

        private static string F(string[] a, int i)
        {
            return (i >= 0 && i < a.Length) ? a[i] : "";
        }

        public static List<Entry> Load(string exeDir)
        {
            List<Entry> list = new List<Entry>();
            string f = PathOf(exeDir);
            if (!File.Exists(f)) return list;
            string[] lines;
            try { lines = File.ReadAllLines(f, Encoding.UTF8); }
            catch { return list; }

            foreach (string line0 in lines)
            {
                if (line0 == null) continue;
                string line = line0.TrimEnd('\r', '\n');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split('\t');
                if (p.Length < 5) continue;

                Entry e = new Entry();
                e.Time = F(p, 0); e.Category = F(p, 1); e.Name = F(p, 2);
                e.Root = F(p, 3); e.Path = F(p, 4); e.Kind = F(p, 5);
                e.Clsid = F(p, 6); e.IconSpec = F(p, 7); e.AppName = F(p, 8);
                e.TargetFile = F(p, 9); e.ItemReg = F(p, 10);
                if (e.Root.Length == 0 || e.Path.Length == 0) continue;
                list.Add(e);
            }
            return list;
        }

        public static void Save(string exeDir, List<Entry> list)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# 右键菜单管家 · 禁用记录\r\n");
            sb.Append("# 由程序自动维护：每次禁用都会记一笔，重新打开软件时这些项会以「已禁用」出现在列表里。\r\n");
            sb.Append("# 删掉某一行 = 忘掉那次禁用（不影响注册表）。恢复某一项请用软件里的「恢复选中项」。\r\n");
            sb.Append("# 时间\t类别\t菜单名称\t根\t路径\t类型\tCLSID\t图标\t软件名\t关联文件\t单项备份\r\n");
            foreach (Entry e in list)
            {
                sb.Append(Clean(e.Time)).Append('\t')
                  .Append(Clean(e.Category)).Append('\t')
                  .Append(Clean(e.Name)).Append('\t')
                  .Append(Clean(e.Root)).Append('\t')
                  .Append(Clean(e.Path)).Append('\t')
                  .Append(Clean(e.Kind)).Append('\t')
                  .Append(Clean(e.Clsid)).Append('\t')
                  .Append(Clean(e.IconSpec)).Append('\t')
                  .Append(Clean(e.AppName)).Append('\t')
                  .Append(Clean(e.TargetFile)).Append('\t')
                  .Append(Clean(e.ItemReg)).Append("\r\n");
            }
            try { File.WriteAllText(PathOf(exeDir), sb.ToString(), new UTF8Encoding(true)); }
            catch { }
        }

        public static void Append(string exeDir, List<Entry> add)
        {
            if (add == null || add.Count == 0) return;
            List<Entry> all = Load(exeDir);
            foreach (Entry e in add)
            {
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(all[i].Root, e.Root, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(all[i].Path, e.Path, StringComparison.OrdinalIgnoreCase))
                        all.RemoveAt(i);
                }
                all.Add(e);
            }
            Save(exeDir, all);
        }

        public static void Remove(string exeDir, string root, string path)
        {
            List<Entry> all = Load(exeDir);
            bool changed = false;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                if (string.Equals(all[i].Root, root, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(all[i].Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    all.RemoveAt(i);
                    changed = true;
                }
            }
            if (changed) Save(exeDir, all);
        }
    }

    // ======================= 系统字符串解析（v2.2） =======================
    //  注册表里大量用 "@shell32.dll,-8506" 这种"资源引用"当显示名，
    //  直接列出来谁也看不懂。这里借系统自带的 SHLoadIndirectString 解析成中文。
    internal static class Loc
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SHLoadIndirectString")]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf,
                                                       int cchOutBuf, IntPtr ppvReserved);

        public static string Resolve(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            if (t.Length == 0) return "";
            if (!t.StartsWith("@")) t = "@" + t;
            try
            {
                StringBuilder sb = new StringBuilder(2048);
                int hr = SHLoadIndirectString(t, sb, sb.Capacity, IntPtr.Zero);
                if (hr == 0 && sb.Length > 0)
                {
                    string r = sb.ToString().Trim();
                    if (r.Length > 0 && r != s) return r;
                }
            }
            catch { }
            return "";
        }

        // 显示名友好化：是资源引用就解析，解析不出来原样返回（不丢信息）
        public static string Friendly(string disp)
        {
            if (string.IsNullOrEmpty(disp)) return disp;
            string d = disp.Trim();
            if (!d.StartsWith("@")) return disp;
            string r = Resolve(d);
            return r.Length > 0 ? r : disp;
        }
    }

    // ======================= 注册表工具 =======================
    internal static class Reg
    {
        public static RegistryKey Hive(string root)
        {
            if (string.Equals(root, "HKCU", StringComparison.OrdinalIgnoreCase)) return Registry.CurrentUser;
            if (string.Equals(root, "HKLM", StringComparison.OrdinalIgnoreCase)) return Registry.LocalMachine;
            return null;
        }

        public static string RootName(string root)
        {
            return string.Equals(root, "HKCU", StringComparison.OrdinalIgnoreCase)
                ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
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

    // ======================= .reg 导出 / 导入 =======================
    internal static class RegFile
    {
        // ---------------- 导出 ----------------
        public static void Export(string file, List<string> rootPaths)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
            foreach (string rp in rootPaths)
            {
                int i = rp.IndexOf('\\');
                if (i < 0) continue;
                string root = rp.Substring(0, i);
                string path = rp.Substring(i + 1);
                RegistryKey k = Reg.Open(root, path, false);
                if (k == null) continue;
                ExportKeyRecursive(sb, root, path, k);
                k.Close();
            }
            UnicodeEncoding enc = new UnicodeEncoding(false, true);
            File.WriteAllText(file, sb.ToString(), enc);
        }

        private static void ExportKeyRecursive(StringBuilder sb, string root, string path, RegistryKey k)
        {
            sb.Append('[').Append(Reg.RootName(root)).Append('\\').Append(path).Append("]\r\n");
            string[] names = k.GetValueNames();
            foreach (string vn in names)
            {
                RegistryValueKind kind;
                object val;
                try
                {
                    kind = k.GetValueKind(vn);
                    val = k.GetValue(vn, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                }
                catch { continue; }
                string line = FormatValue(vn, kind, val);
                if (line != null) sb.Append(line).Append("\r\n");
            }
            sb.Append("\r\n");
            string[] subs = k.GetSubKeyNames();
            foreach (string sub in subs)
            {
                RegistryKey sk = null;
                try { sk = k.OpenSubKey(sub); }
                catch { }
                if (sk != null)
                {
                    ExportKeyRecursive(sb, root, path + "\\" + sub, sk);
                    sk.Close();
                }
            }
        }

        private static string EscapeReg(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string HexOf(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(b[i].ToString("x2"));
            }
            return sb.ToString();
        }

        private static string FormatValue(string name, RegistryValueKind kind, object val)
        {
            string prefix = (name.Length == 0) ? "@" : "\"" + EscapeReg(name) + "\"";
            if (val == null) return null;
            switch (kind)
            {
                case RegistryValueKind.String:
                    return prefix + "=\"" + EscapeReg(Convert.ToString(val)) + "\"";
                case RegistryValueKind.ExpandString:
                    return prefix + "=hex(2):" + HexOf(Encoding.Unicode.GetBytes(Convert.ToString(val) + "\0"));
                case RegistryValueKind.MultiString:
                    {
                        string[] arr = val as string[];
                        if (arr == null) return null;
                        StringBuilder t = new StringBuilder();
                        foreach (string s in arr) t.Append(s).Append('\0');
                        t.Append('\0');
                        return prefix + "=hex(7):" + HexOf(Encoding.Unicode.GetBytes(t.ToString()));
                    }
                case RegistryValueKind.DWord:
                    return prefix + "=dword:" + ((int)Convert.ToInt32(val)).ToString("x8");
                case RegistryValueKind.QWord:
                    return prefix + "=hex(b):" + HexOf(BitConverter.GetBytes(Convert.ToInt64(val)));
                case RegistryValueKind.Binary:
                    return prefix + "=hex:" + HexOf((byte[])val);
                default:
                    return null;
            }
        }

        // ---------------- 读取（自动识别编码） ----------------
        public static string ReadAllTextAuto(string file)
        {
            byte[] b = File.ReadAllBytes(file);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
                return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            return Encoding.UTF8.GetString(b);
        }

        private static byte[] HexToBytes(string s)
        {
            s = s.Replace(",", "").Replace(" ", "").Trim();
            if (s.Length % 2 != 0) s = "0" + s;
            byte[] r = new byte[s.Length / 2];
            for (int i = 0; i < r.Length; i++)
                r[i] = byte.Parse(s.Substring(i * 2, 2), NumberStyles.HexNumber);
            return r;
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static void SetValueFromReg(RegistryKey k, string name, string valPart)
        {
            if (valPart.StartsWith("\""))
            {
                k.SetValue(name, Unquote(valPart), RegistryValueKind.String);
                return;
            }
            int c = valPart.IndexOf(':');
            if (c < 0) return;
            string type = valPart.Substring(0, c).ToLowerInvariant();
            string data = valPart.Substring(c + 1);

            if (type == "dword")
            {
                k.SetValue(name, (int)long.Parse(data, NumberStyles.HexNumber), RegistryValueKind.DWord);
            }
            else if (type == "hex(b)")
            {
                byte[] b = HexToBytes(data);
                long q = BitConverter.ToInt64(b, 0);
                k.SetValue(name, q, RegistryValueKind.QWord);
            }
            else if (type == "hex(2)")
            {
                string s = Encoding.Unicode.GetString(HexToBytes(data));
                s = s.TrimEnd('\0');
                k.SetValue(name, s, RegistryValueKind.ExpandString);
            }
            else if (type == "hex(7)")
            {
                string s = Encoding.Unicode.GetString(HexToBytes(data));
                string[] parts = s.Split('\0');
                List<string> lst = new List<string>();
                foreach (string p in parts) if (p.Length > 0) lst.Add(p);
                k.SetValue(name, lst.ToArray(), RegistryValueKind.MultiString);
            }
            else if (type == "hex")
            {
                k.SetValue(name, HexToBytes(data), RegistryValueKind.Binary);
            }
            else if (type.StartsWith("hex("))
            {
                k.SetValue(name, HexToBytes(data), RegistryValueKind.Binary);
            }
        }

        // ---------------- 导入 ----------------
        public static int Import(string file, List<string> errors)
        {
            string text = ReadAllTextAuto(file);
            string[] raw = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            List<string> lines = new List<string>();
            StringBuilder acc = new StringBuilder();
            foreach (string r in raw)
            {
                string t = r.TrimEnd();
                if (t.EndsWith("\\")) { acc.Append(t.Substring(0, t.Length - 1)); continue; }
                acc.Append(t);
                lines.Add(acc.ToString());
                acc.Length = 0;
            }
            if (acc.Length > 0) lines.Add(acc.ToString());

            string curRoot = null;
            string curPath = null;
            int applied = 0;

            foreach (string line in lines)
            {
                string s = line.Trim();
                if (s.Length == 0) continue;
                if (s.StartsWith(";") || s.StartsWith("#")) continue;
                if (s.StartsWith("Windows Registry Editor")) continue;

                if (s.StartsWith("["))
                {
                    bool del = s.StartsWith("[-");
                    string inner = s.Substring(del ? 2 : 1);
                    int close = inner.LastIndexOf(']');
                    if (close >= 0) inner = inner.Substring(0, close);
                    int i = inner.IndexOf('\\');
                    string root = (i < 0) ? inner : inner.Substring(0, i);
                    string path = (i < 0) ? "" : inner.Substring(i + 1);
                    curRoot = Reg.NormRoot(root);
                    curPath = path;
                    if (curRoot == null) continue;
                    RegistryKey h = Reg.Hive(curRoot);
                    if (del)
                    {
                        try { h.DeleteSubKeyTree(path); applied++; }
                        catch (Exception ex) { if (errors != null) errors.Add(path + " : " + ex.Message); }
                    }
                    else
                    {
                        try { RegistryKey nk = h.CreateSubKey(path); if (nk != null) nk.Close(); }
                        catch (Exception ex) { if (errors != null) errors.Add(path + " : " + ex.Message); }
                    }
                    continue;
                }

                int eq = s.IndexOf('=');
                if (eq < 0) continue;
                if (curRoot == null || curPath == null) continue;

                string namePart = s.Substring(0, eq).Trim();
                string valPart = s.Substring(eq + 1).Trim();
                string valueName = (namePart == "@") ? "" : Unquote(namePart);

                RegistryKey k = Reg.Open(curRoot, curPath, true);
                if (k == null) continue;
                try
                {
                    if (valPart == "-") { k.DeleteValue(valueName, false); applied++; }
                    else { SetValueFromReg(k, valueName, valPart); applied++; }
                }
                catch (Exception ex) { if (errors != null) errors.Add(curPath + " :: " + valueName + " : " + ex.Message); }
                k.Close();
            }
            return applied;
        }
    }

    // ======================= 扫描器 =======================
    internal static class Scanner
    {
        public const string RecycleClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
        public const string ThisPcClsid = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        public const string BlockedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

        private static readonly string[] SystemNames = new string[] {
            "open","opennewprocess","opennewwindow","opennewtab","print","printto","runas","find",
            "cmd","powershell","pintohome","pinstart","pin to start","explore","copypath","share",
            "cast to device","troubleshootcompat","compatibility","windows.modernshare","give access to",
            "include in library","manage-bde","encrypt-bde","encrypt-bde-elev","change-passphrase",
            "change-pin","resume-bde","resume-bde-elev","unlock-bde","manage","none","restore",
            "cut","copy","paste","delete","rename","updateencryptionsettings","modernproperties",
            "sendto","openwith","new","windows.share","windows.print","addtoplaylist","play",
            "addlibrary","mapliben","restore","workfolders","scanwithwindowsdefender",
            "opencontainingfolder","openfolderlocation","shellhomegroup","homegroup",
            "windowsscanwithdefender","windowsdefenderscan","encrypt","efsdecrypt",
            "removeproperties"
        };

        public static bool IsSystemName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            foreach (string s in SystemNames) if (n == s) return true;
            return false;
        }

        public static HashSet<string> GetBlockedSet()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in new string[] { "HKLM", "HKCU" })
            {
                RegistryKey k = Reg.Open(root, BlockedPath, false);
                if (k == null) continue;
                foreach (string vn in k.GetValueNames()) set.Add(vn.ToUpperInvariant());
                k.Close();
            }
            return set;
        }

        // CLSID -> 友好名 / 归属信息
        public class ClsidInfo
        {
            public string Name = "";
            public string Server = "";
            public string Package = "";
            public string DefaultIcon = "";   // HKCR\CLSID\{..}\DefaultIcon
            public bool Found;
            public bool IsWindows;
        }

        private static Dictionary<string, ClsidInfo> _clsidCache = new Dictionary<string, ClsidInfo>();

        public static ClsidInfo GetClsidInfo(string clsid)
        {
            if (_clsidCache.ContainsKey(clsid)) return _clsidCache[clsid];
            ClsidInfo info = new ClsidInfo();
            string key = @"SOFTWARE\Classes\CLSID\" + clsid;

            foreach (string root in new string[] { "HKCU", "HKLM" })
            {
                RegistryKey k = Reg.Open(root, key, false);
                if (k == null) continue;
                info.Found = true;
                if (info.Name.Length == 0) info.Name = Reg.GetStr(k, "");
                foreach (string srv in new string[] { "InProcServer32", "LocalServer32" })
                {
                    RegistryKey sk = null;
                    try { sk = k.OpenSubKey(srv); } catch { }
                    if (sk != null)
                    {
                        string p = Reg.GetStr(sk, "");
                        if (p.Length > 0 && info.Server.Length == 0) info.Server = p;
                        sk.Close();
                    }
                }

                // 外壳用的图标规格（Explorer 画右键菜单时用的就是它）
                if (info.DefaultIcon.Length == 0)
                {
                    RegistryKey dik = null;
                    try { dik = k.OpenSubKey("DefaultIcon"); } catch { }
                    if (dik != null)
                    {
                        string di = Reg.GetStr(dik, "");
                        if (di.Length > 0) info.DefaultIcon = di;
                        dik.Close();
                    }
                }
                k.Close();
            }

            // PackagedCom（MSIX 打包扩展）
            try
            {
                RegistryKey pk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\PackagedCom\Package");
                if (pk != null)
                {
                    foreach (string pkg in pk.GetSubKeyNames())
                    {
                        RegistryKey ck = null;
                        try { ck = pk.OpenSubKey(pkg + @"\Class\" + clsid); }
                        catch { }
                        if (ck != null)
                        {
                            info.Found = true;
                            info.Package = pkg;
                            if (info.Name.Length == 0)
                            {
                                RegistryKey sp = null;
                                try { sp = pk.OpenSubKey(pkg + @"\Server\0"); }
                                catch { }
                                if (sp != null)
                                {
                                    string dn = Reg.GetStr(sp, "DisplayName");
                                    if (dn.Length > 0) info.Name = dn;
                                    sp.Close();
                                }
                                if (info.Name.Length == 0) info.Name = pkg;
                            }
                            ck.Close();
                            break;
                        }
                    }
                    pk.Close();
                }
            }
            catch { }

            // 是否 Windows 自带
            if (Reg.IsWindowsPath(info.Server)) info.IsWindows = true;
            if (info.Package.Length > 0 &&
                (info.Package.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                 info.Package.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)))
                info.IsWindows = true;
            if (info.Name.Length > 0 &&
                (info.Name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                 info.Name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)))
                info.IsWindows = true;
            if (!info.IsWindows && info.Found && info.Name.Length > 0 && !Reg.Exists("HKCU", key) && info.Server.Length == 0)
                info.IsWindows = true;   // 仅 HKLM 注册且无服务路径 -> 多半系统组件

            _clsidCache[clsid] = info;
            return info;
        }

        public static string FriendlyName(string clsid)
        {
            ClsidInfo ci = GetClsidInfo(clsid);
            if (ci.Name.Length > 0 && !ci.Name.StartsWith("{")) return ci.Name;
            return clsid;
        }

        // 从 DLL 路径 / 打包名 里猜出软件名（给用户看）
        public static string GuessAppName(ClsidInfo ci)
        {
            string p = ci.Server;
            if (!string.IsNullOrEmpty(p))
            {
                string[] markers = new string[] {
                    "\\Program Files (x86)\\", "\\Program Files\\",
                    "\\AppData\\Local\\", "\\AppData\\Roaming\\", "\\ProgramData\\"
                };
                foreach (string m in markers)
                {
                    int i = p.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        string rest = p.Substring(i + m.Length);
                        int j = rest.IndexOf('\\');
                        if (j > 0) return NormalizeAppName(rest.Substring(0, j));
                        if (rest.Length > 0) return NormalizeAppName(rest);
                    }
                }
                try
                {
                    string fn = Path.GetFileNameWithoutExtension(p);
                    if (!string.IsNullOrEmpty(fn)) return NormalizeAppName(fn);
                }
                catch { }
            }
            if (ci.Package.Length > 0)
            {
                string s = ci.Package;
                int u = s.IndexOf('_');
                if (u > 0) s = s.Substring(0, u);
                int d = s.IndexOf('.');
                if (d > 0) s = s.Substring(0, d);
                return NormalizeAppName(s);
            }
            return "";
        }

        // 常见软件名规范化
        private static string NormalizeAppName(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string low = s.ToLowerInvariant();
            if (low.StartsWith("baidu")) return "百度网盘";
            if (low.StartsWith("wps") || low.StartsWith("kingsoft") || low.StartsWith("kwps")) return "WPS Office";
            if (low == "qq" || low.StartsWith("qq")) return "QQ";
            if (low.StartsWith("todesk")) return "ToDesk";
            if (low.StartsWith("douyin")) return "抖音";
            if (low.StartsWith("wiztree")) return "WizTree";
            if (low.StartsWith("winrar")) return "WinRAR";
            if (low.StartsWith("360")) return "360";
            if (low.StartsWith("tencent")) return "腾讯";
            if (low.StartsWith("thunder")) return "迅雷";
            if (low.StartsWith("netease")) return "网易";
            if (low.StartsWith("alibaba") || low.StartsWith("ali")) return "阿里";
            if (low.StartsWith("amd") || low.StartsWith("advancedmicro")) return "AMD";
            if (low.StartsWith("nvidia")) return "NVIDIA";
            if (low.StartsWith("microsoft") || low.StartsWith("windows")) return "Windows";
            if (s.Length == 1) return s.ToUpperInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // 组装给用户看的显示名：软件名 + 功能名
        public static string BuildDisplayName(string clsid, string keyName)
        {
            ClsidInfo ci = GetClsidInfo(clsid);
            string raw = ci.Name.Length > 0 ? ci.Name : keyName;
            raw = raw.Replace(" Class", "").Replace("ContextMenu", "").Trim();
            if (raw.Length == 0) raw = keyName;

            string app = GuessAppName(ci);
            if (app.Length > 0 &&
                raw.IndexOf(app, StringComparison.OrdinalIgnoreCase) < 0)
                return app + " — " + raw;
            return raw;
        }

        // ---------------- 图标规格 / 归属软件 ----------------

        public const string DesktopClsid = "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}";

        // 直接读某个 CLSID 的 DefaultIcon（"C:\..\x.dll,-12" 形式）
        public static string DefaultIconOf(string clsid)
        {
            if (string.IsNullOrEmpty(clsid)) return "";
            string key = @"SOFTWARE\Classes\CLSID\" + clsid;
            foreach (string root in new string[] { "HKCU", "HKLM" })
            {
                RegistryKey k = Reg.Open(root, key, false);
                if (k == null) continue;
                string v = "";
                RegistryKey dik = null;
                try { dik = k.OpenSubKey("DefaultIcon"); } catch { }
                if (dik != null) { v = Reg.GetStr(dik, ""); dik.Close(); }
                k.Close();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return "";
        }

        private static bool IsContainerName(string s)
        {
            switch ((s == null ? "" : s).ToLowerInvariant())
            {
                case "programs":
                case "packages":
                case "microsoft":
                case "windows":
                case "common files":
                case "apps":
                case "temp":
                case "local":
                case "roaming":
                case "program files":
                case "program files (x86)":
                    return true;
                default:
                    return false;
            }
        }

        // 从文件夹结构里推软件名（"C:\Program Files\WPS Office\x.dll" -> "WPS Office"）
        public static string AppNameFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            path = StripIndex(path);
            try { path = Environment.ExpandEnvironmentVariables(path); }
            catch { }
            // 裸文件名（imageres.dll 这种）先还原成 System32 下的完整路径，
            // 否则会被误判成"第三方软件 Imageres"
            string resolved = IconLib.ResolveFile(path);
            if (resolved.Length > 0) path = resolved;
            if (Reg.IsWindowsPath(path)) return "Windows";

            string[] markers = new string[] {
                "\\Program Files (x86)\\", "\\Program Files\\",
                "\\AppData\\Local\\", "\\AppData\\Roaming\\", "\\ProgramData\\"
            };
            foreach (string m in markers)
            {
                int i = path.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                string rest = path.Substring(i + m.Length);
                string seg = "";
                int guard = 0;
                while (rest.Length > 0 && guard++ < 4)
                {
                    int j = rest.IndexOf('\\');
                    if (j < 0) { seg = rest; break; }
                    seg = rest.Substring(0, j);
                    if (!IsContainerName(seg)) break;
                    rest = rest.Substring(j + 1);
                    if (rest.IndexOf('\\') < 0) { seg = rest; break; }
                }
                if (seg.Length > 0) return NormalizeAppName(seg);
            }
            try
            {
                string fn = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrEmpty(fn)) return NormalizeAppName(fn);
            }
            catch { }
            return "";
        }

        // 综合判断"这项属于哪个软件"
        public static string AppNameOf(ClsidInfo ci, string disp, string spec)
        {
            if (ci != null)
            {
                string a = GuessAppName(ci);
                if (a.Length > 0) return a;
                a = AppNameFromPath(ci.Server);
                if (a.Length > 0) return a;
                if (ci.IsWindows) return "Windows";
            }
            string fromSpec = AppNameFromPath(StripIndex(spec));
            if (fromSpec.Length > 0) return fromSpec;
            if (!string.IsNullOrEmpty(disp))
            {
                int e = disp.IndexOf(" — ", StringComparison.Ordinal);
                if (e > 0) return disp.Substring(0, e).Trim();
            }
            return "";
        }

        // "C:\a\b.dll,-12" -> "C:\a\b.dll"
        public static string StripIndex(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return "";
            string s = spec.Trim();
            if (s.Length > 0 && s[0] == '@') s = s.Substring(1).Trim();
            s = s.Trim('"').Trim();
            int comma = s.LastIndexOf(',');
            if (comma > 0)
            {
                string tail = s.Substring(comma + 1).Trim();
                int v;
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    s = s.Substring(0, comma).Trim().Trim('"').Trim();
            }
            return s;
        }

        // 从命令行里挖出可执行文件 / dll（拿它的图标）
        private static string FirstAssetFromCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            string first;
            string s = cmd.Trim();
            if (s.StartsWith("\""))
            {
                int q = s.IndexOf('"', 1);
                first = (q > 1) ? s.Substring(1, q - 1) : s.Trim('"');
            }
            else
            {
                int sp = s.IndexOf(' ');
                first = (sp > 0) ? s.Substring(0, sp) : s;
            }
            try { first = Environment.ExpandEnvironmentVariables(first); }
            catch { }
            // rundll32 / cmd / mshta 只是"宿主"，它们的图标不代表这个软件
            if (LooksLikeAsset(first) && FileExists(first) && !IsHostProgramName(first))
                return first;

            // 裸命令名（notepad、cmd 之类，不带路径）：到 System32 / Windows 目录里找
            if (first.IndexOf('\\') < 0 && LooksLikeAsset(first))
            {
                string bare = ResolveCommandName(first);
                if (bare.Length > 0 && !IsHostProgramName(bare)) return bare;
            }

            // rundll32 / mshta 之类：往后找 dll / 真正的主程序 exe
            char[] sep = new char[] { ' ', ',', '\t' };
            string[] toks = s.Split(sep);
            string hostFallback = "";
            string dllFallback = "";
            for (int i = 0; i < toks.Length; i++)
            {
                string t = toks[i].Trim().Trim('"').Trim();
                if (t.Length < 4) continue;
                string tl = t.ToLowerInvariant();
                if (tl.EndsWith(".dll") || tl.EndsWith(".exe"))
                {
                    string full = t;
                    int slash = full.IndexOf('\\');
                    if (slash > 0)
                    {
                        try { full = Environment.ExpandEnvironmentVariables(full); }
                        catch { }
                        if (FileExists(full))
                        {
                            if (IsHostProgramName(full))
                            {
                                if (hostFallback.Length == 0) hostFallback = full;
                                continue;
                            }
                            if (tl.EndsWith(".exe")) return full;        // 主程序优先
                            if (dllFallback.Length == 0) dllFallback = full;
                        }
                    }
                }
            }
            if (dllFallback.Length > 0) return dllFallback;
            return hostFallback;
        }

        private static bool IsHostProgramName(string path)
        {
            string n;
            try { n = Path.GetFileName(path).ToLowerInvariant(); }
            catch { return false; }
            return n == "rundll32.exe" || n == "cmd.exe" || n == "mshta.exe" ||
                   n == "cscript.exe" || n == "wscript.exe" || n == "conhost.exe" ||
                   n == "regsvr32.exe" || n == "powershell.exe" || n == "pwsh.exe" ||
                   n == "explorer.exe";
        }

        private static bool LooksLikeAsset(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            string l = p.ToLowerInvariant();
            return (l.EndsWith(".exe") || l.EndsWith(".dll") || l.EndsWith(".ico"));
        }

        private static bool FileExists(string p)
        {
            try { return p.IndexOf('\\') > 0 && File.Exists(p); }
            catch { return false; }
        }

        // 传统 shell 键：优先用键里的 Icon 值，其次从 command 里挖。
        // 但 Icon 值若指向系统通用图标（imageres.dll,-77 盾牌之类），它代表不了
        // 这个软件 —— 这时优先用命令行里的主程序（v3.0 修"隐藏后图标变了"）。
        private static string PickSpecForShellKey(string icon, string cmd)
        {
            if (!string.IsNullOrEmpty(icon))
            {
                string trimmed = icon.Trim();
                if (!IconLib.IsSystemIconSpec(trimmed)) return trimmed;
                string fromCmd = FirstAssetFromCommand(cmd);
                if (fromCmd.Length > 0) return fromCmd;
                return trimmed;
            }
            return FirstAssetFromCommand(cmd);
        }

        // shell 扩展 / 命名空间：优先 DefaultIcon，其次服务 DLL/exe
        private static string PickSpecForClsid(ClsidInfo ci)
        {
            if (ci == null) return "";
            if (!string.IsNullOrEmpty(ci.DefaultIcon)) return ci.DefaultIcon.Trim();
            return ci.Server;
        }

        // ---------------- 关联文件解析（右键"打开文件所在位置"用） ----------------

        // 把 "C:\a\b.dll,-12" / "@shell32.dll,-8506" / "app.exe,0" 还原成真实存在的文件路径
        public static string FileOfSpec(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return "";
            string s = spec;
            try { s = StripIndex(s); } catch { }
            s = s.Trim().Trim('"').Trim();
            if (s.StartsWith("@")) s = s.Substring(1).Trim();
            try { s = IconLib.ResolveFile(s); } catch { }
            if (FileExists(s)) return s;
            return "";
        }

        // 从一段任意文本里揪出第一个真实存在的 .exe/.dll/.ico 全路径
        // （外壳扩展的服务项常写成 "C:\Program Files\App\x.dll" -Embedding）
        public static string FindFilePathIn(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s;
            try { s = Environment.ExpandEnvironmentVariables(text); }
            catch { s = text; }

            try
            {
                Match m = Regex.Match(s, "[A-Za-z]:\\\\[^\"\\r\\n]*?\\.(?:dll|exe|ico)",
                                      RegexOptions.IgnoreCase);
                while (m.Success)
                {
                    string cand = m.Value.Trim();
                    if (FileExists(cand)) return cand;
                    m = m.NextMatch();
                }
            }
            catch { }

            string whole = s.Trim().Trim('"');
            if (LooksLikeAsset(whole) && FileExists(whole)) return whole;
            return "";
        }

        // 裸命令名（notepad、cmd 之类）：到 System32 / Windows 目录里找同名 exe
        private static string ResolveCommandName(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            string s = cmd.Trim();
            if (s.StartsWith("\"")) s = s.Substring(1);
            int sp = s.IndexOfAny(new char[] { ' ', '\t', ',', '\\' });
            string name = (sp > 0) ? s.Substring(0, sp) : s;
            name = name.Trim().Trim('"');
            if (name.Length == 0) return "";

            string low = name.ToLowerInvariant();
            if (low.EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
            else if (name.IndexOf('.') >= 0) return "";   // 带别的扩展名，不当命令处理

            string win = "";
            try { win = Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
            catch { }
            if (win.Length == 0) return "";

            string[] cand = new string[] {
                Path.Combine(win, "System32", name + ".exe"),
                Path.Combine(win, name + ".exe")
            };
            foreach (string c in cand) if (FileExists(c)) return c;
            return "";
        }

        // 外壳扩展会在右键菜单里显示成什么文字？去它的 STRINGTABLE 资源里找
        private static void FillMenuNames(RightMenuItem mi, string server, string regName)
        {
            if (mi == null || mi.IsSystem) return;
            string dll = "";
            try { dll = FileOfSpec(server); } catch { }
            if (dll.Length == 0) return;
            if (!dll.ToLowerInvariant().EndsWith(".dll")) return;

            List<string> names = DllText.Candidates(dll);
            if (names.Count == 0) return;

            mi.MenuNames = names;
            mi.MenuHint = DllText.Join(names, 2);

            string app = string.IsNullOrEmpty(mi.AppName) ? "" : mi.AppName;
            string head = names[0];
            if (app.Length > 0) mi.DisplayName = app + " ｜ " + head;
            else mi.DisplayName = head;
        }

        // 一项到底关联着哪个文件：图标来源 -> 命令行 -> 服务 DLL
        // （图标值若只是个 .ico 图标资源，不能代表"这个软件"，这时优先看命令行）
        private static string ResolveTarget(string spec, string server, string cmd)
        {
            string f = FileOfSpec(spec);
            bool iconUsable = (f.Length > 0 && !f.ToLowerInvariant().EndsWith(".ico"));
            if (iconUsable) return f;

            string c = FindFilePathIn(cmd);
            if (c.Length == 0) c = ResolveCommandName(cmd);
            if (c.Length > 0) return c;

            f = FindFilePathIn(server);
            if (f.Length > 0) return f;

            return FileOfSpec(spec);   // 最后才退回 .ico
        }

        // ---------------- 主扫描 ----------------
        private class ShellTarget
        {
            public string Cat;
            public string Root;
            public string Path;
            public bool IsEx;
            public ShellTarget(string cat, string root, string path, bool isEx)
            { Cat = cat; Root = root; Path = path; IsEx = isEx; }
        }

        public static List<RightMenuItem> Scan()
        {
            _clsidCache.Clear();
            List<RightMenuItem> list = new List<RightMenuItem>();
            HashSet<string> blocked = GetBlockedSet();
            string[] roots = new string[] { "HKCU", "HKLM" };

            List<ShellTarget> targets = new List<ShellTarget>();

            // 传统 shell 键
            string[] shellPaths = new string[] {
                @"SOFTWARE\Classes\Directory\Background\shell",
                @"SOFTWARE\Classes\Directory\shell",
                @"SOFTWARE\Classes\*\shell",
                @"SOFTWARE\Classes\Folder\shell",
                @"SOFTWARE\Classes\Drive\shell",
                @"SOFTWARE\Classes\AllFilesystemObjects\shell"
            };
            string[] shellCats = new string[] { "文件夹空白处", "文件夹", "文件", "文件夹", "驱动器", "所有对象" };

            for (int i = 0; i < shellPaths.Length; i++)
                foreach (string r in roots)
                    targets.Add(new ShellTarget(shellCats[i], r, shellPaths[i], false));

            // 此电脑 / 回收站 的静态项
            targets.Add(new ShellTarget("此电脑", "HKLM", @"SOFTWARE\Classes\CLSID\" + ThisPcClsid + @"\shell", false));
            targets.Add(new ShellTarget("回收站", "HKLM", @"SOFTWARE\Classes\CLSID\" + RecycleClsid + @"\shell", false));
            targets.Add(new ShellTarget("回收站", "HKCU", @"SOFTWARE\Classes\CLSID\" + RecycleClsid + @"\shell", false));

            // 外壳扩展 shellex\ContextMenuHandlers
            string[] exPaths = new string[] {
                @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Directory\Background\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Drive\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\AllFilesystemObjects\shellex\ContextMenuHandlers"
            };
            string[] exCats = new string[] { "文件", "文件夹", "文件夹空白处", "文件夹", "驱动器", "所有对象" };
            for (int i = 0; i < exPaths.Length; i++)
                foreach (string r in roots)
                    targets.Add(new ShellTarget(exCats[i], r, exPaths[i], true));

            targets.Add(new ShellTarget("回收站", "HKLM", @"SOFTWARE\Classes\CLSID\" + RecycleClsid + @"\shellex\ContextMenuHandlers", true));
            targets.Add(new ShellTarget("此电脑", "HKLM", @"SOFTWARE\Classes\CLSID\" + ThisPcClsid + @"\shellex\ContextMenuHandlers", true));

            foreach (ShellTarget t in targets)
            {
                RegistryKey baseKey = Reg.Open(t.Root, t.Path, false);
                if (baseKey == null) continue;
                string[] names;
                try { names = baseKey.GetSubKeyNames(); }
                catch { names = new string[0]; }

                foreach (string name in names)
                {
                    if (!t.IsEx)
                    {
                        RegistryKey k = null;
                        try { k = baseKey.OpenSubKey(name); }
                        catch { }
                        if (k == null) continue;
                        string disp = Reg.GetStr(k, "");
                        string muiverb = Reg.GetStr(k, "MUIVerb");
                        string icon = Reg.GetStr(k, "Icon");
                        RegistryKey ck = null;
                        try { ck = k.OpenSubKey("command"); } catch { }
                        string cmd = Reg.GetStr(ck, "");
                        if (ck != null) ck.Close();
                        k.Close();

                        if (disp.Length == 0) disp = muiverb;
                        if (disp.Length == 0) disp = name;

                        bool isSys = IsSystemName(name);
                        if (!isSys && (muiverb.StartsWith("@") || disp.StartsWith("@"))) isSys = true;
                        if (!isSys && Reg.IsWindowsPath(cmd)) isSys = true;
                        if (!isSys && Reg.IsWindowsPath(icon)) isSys = true;

                        // v2.2：把 "@shell32.dll,-8506" 这类资源引用解析成真正的中文名
                        disp = Loc.Friendly(disp);

                        RightMenuItem mi = new RightMenuItem();
                        mi.Category = t.Cat;
                        mi.DisplayName = disp;
                        mi.Root = t.Root;
                        mi.Path = t.Path + "\\" + name;
                        mi.Kind = ItemKind.ShellKey;
                        mi.IsSystem = isSys;
                        mi.Detail = cmd.Length > 0 ? cmd : ("Key: " + name);
                        mi.IconSpec = PickSpecForShellKey(icon, cmd);
                        mi.AppName = AppNameOf(null, disp, mi.IconSpec);
                        mi.TargetFile = ResolveTarget(mi.IconSpec, "", cmd);
                        list.Add(mi);
                    }
                    else
                    {
                        RegistryKey k = null;
                        try { k = baseKey.OpenSubKey(name); }
                        catch { }
                        if (k == null) continue;
                        string val = Reg.GetStr(k, "");
                        k.Close();
                        if (string.IsNullOrEmpty(val)) continue;

                        string clsid = val.Trim().ToUpperInvariant();
                        if (!clsid.StartsWith("{")) continue;

                        ClsidInfo ci = GetClsidInfo(clsid);
                        string disp = BuildDisplayName(clsid, name);

                        RightMenuItem mi = new RightMenuItem();
                        mi.Category = t.Cat;
                        mi.DisplayName = disp;
                        mi.Root = t.Root;
                        mi.Path = t.Path;
                        mi.Kind = ItemKind.ShellEx;
                        mi.Clsid = clsid;
                        mi.IsBlocked = blocked.Contains(clsid);
                        mi.IsSystem = ci.IsWindows;
                        mi.Detail = clsid + "   [" + name + "]";
                        mi.IconSpec = PickSpecForClsid(ci);
                        mi.AppName = AppNameOf(ci, disp, mi.IconSpec);
                        mi.TargetFile = ResolveTarget(mi.IconSpec, ci.Server, "");
                        FillMenuNames(mi, ci.Server, name);
                        if (mi.MenuNames.Count > 1)
                            mi.Detail = "也叫：" + DllText.Join(mi.MenuNames, 3);
                        list.Add(mi);
                    }
                }
                baseKey.Close();
            }

            // 命名空间（此电脑 / 桌面 的图标入口）
            string[] nsPaths = new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace"
            };
            string[] nsCats = new string[] { "此电脑图标", "桌面图标" };

            for (int i = 0; i < nsPaths.Length; i++)
            {
                foreach (string r in roots)
                {
                    RegistryKey bk = Reg.Open(r, nsPaths[i], false);
                    if (bk == null) continue;
                    string[] names;
                    try { names = bk.GetSubKeyNames(); }
                    catch { names = new string[0]; }
                    foreach (string name in names)
                    {
                        RegistryKey k = null;
                        try { k = bk.OpenSubKey(name); }
                        catch { }
                        if (k == null) continue;
                        string disp = Reg.GetStr(k, "");
                        if (disp.Length == 0) disp = Reg.GetStr(k, "NAME");
                        if (disp.Length == 0) disp = name;
                        k.Close();

                        bool isSys = (r == "HKLM");
                        if (disp.StartsWith("CLSID_")) isSys = true;
                        if (disp == name) isSys = true;

                        disp = Loc.Friendly(disp);   // 同上：资源引用 -> 中文名

                        RightMenuItem mi = new RightMenuItem();
                        mi.Category = nsCats[i];
                        mi.DisplayName = disp;
                        mi.Root = r;
                        mi.Path = nsPaths[i] + "\\" + name;
                        mi.Kind = ItemKind.NameSpace;
                        mi.Clsid = name;
                        mi.IsSystem = isSys;
                        mi.Detail = FriendlyName(name);
                        ClsidInfo nci = null;
                        if (name.StartsWith("{")) nci = GetClsidInfo(name);
                        mi.IconSpec = (nci != null) ? PickSpecForClsid(nci) : "";
                        mi.AppName = AppNameOf(nci, disp, mi.IconSpec);
                        mi.TargetFile = ResolveTarget(mi.IconSpec, (nci != null) ? nci.Server : "", "");
                        list.Add(mi);
                    }
                    bk.Close();
                }
            }

            // 已屏蔽列表（单独列出，方便解除）
            foreach (string r in new string[] { "HKLM", "HKCU" })
            {
                RegistryKey k = Reg.Open(r, BlockedPath, false);
                if (k == null) continue;
                string[] vals;
                try { vals = k.GetValueNames(); }
                catch { vals = new string[0]; }
                foreach (string vn in vals)
                {
                    string clsid = vn.Trim().ToUpperInvariant();
                    if (!clsid.StartsWith("{")) continue;
                    bool already = false;
                    foreach (RightMenuItem x in list)
                        if (x.Kind == ItemKind.ShellEx && x.Clsid == clsid) { already = true; break; }
                    if (already) continue;

                    RightMenuItem mi = new RightMenuItem();
                    mi.Category = "已屏蔽";
                    mi.DisplayName = BuildDisplayName(clsid, "");
                    mi.Root = r;
                    mi.Path = BlockedPath;
                    mi.Kind = ItemKind.ShellEx;
                    mi.Clsid = clsid;
                    mi.IsBlocked = true;
                    mi.IsSystem = false;
                    mi.Detail = clsid + "   (屏蔽记录)";
                    ClsidInfo bci = GetClsidInfo(clsid);
                    mi.IconSpec = PickSpecForClsid(bci);
                    mi.AppName = AppNameOf(bci, mi.DisplayName, mi.IconSpec);
                    mi.TargetFile = ResolveTarget(mi.IconSpec, (bci != null) ? bci.Server : "", "");
                    FillMenuNames(mi, (bci != null) ? bci.Server : "", "");
                    if (mi.MenuNames.Count > 1)
                        mi.Detail = "也叫：" + DllText.Join(mi.MenuNames, 3) + "   (屏蔽记录)";
                    list.Add(mi);
                }
                k.Close();
            }

            return list;
        }

        // ---------------- 从旧备份里反推禁用记录 ----------------
        //  v2.3 之前的版本没有 disabled.tsv，但 backups\<时间戳>\restore.reg 里
        //  明明白白写着当时删掉了哪些键。把它们补成记录，这样：
        //    · 老用户以前禁用过的东西，升级后也能在列表里看到；
        //    · 别人拿到一份旧备份 + 程序，照样能看出"这里原本有项、怎么还原"。
        public static int ImportHistoryFromBackups(string exeDir, string backupRoot)
        {
            if (!Directory.Exists(backupRoot)) return 0;

            List<DisabledLog.Entry> known = DisabledLog.Load(exeDir);
            HashSet<string> knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DisabledLog.Entry e in known) knownKeys.Add(e.Root + "\\" + e.Path);

            List<DisabledLog.Entry> add = new List<DisabledLog.Entry>();
            string[] dirs;
            try { dirs = Directory.GetDirectories(backupRoot); }
            catch { return 0; }

            foreach (string d in dirs)
            {
                string reg = System.IO.Path.Combine(d, "restore.reg");
                if (!File.Exists(reg)) continue;

                string stamp = System.IO.Path.GetFileName(d);
                string time = FormatStamp(stamp);

                List<string> keys = new List<string>();
                List<string> sections = new List<string>();
                try { SplitRegFile(reg, keys, sections); }
                catch { continue; }

                for (int i = 0; i < keys.Count && i < sections.Count; i++)
                {
                    string kp = keys[i];
                    if (kp.IndexOf("Shell Extensions\\Blocked", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;   // 屏蔽记录不是"删键"，另有显示

                    string hive = TopHive(kp);
                    string root = Reg.NormRoot(hive);
                    if (root == null) continue;
                    string path = kp.Substring(hive.Length).TrimStart('\\');
                    if (path.Length == 0) continue;
                    if (knownKeys.Contains(root + "\\" + path)) continue;

                    // 导出是递归的：如果父键也在这一批里，子键就不用单独记了
                    bool hasParent = false;
                    for (int j = 0; j < keys.Count; j++)
                    {
                        if (j == i) continue;
                        string other = keys[j];
                        if (other.Length < kp.Length &&
                            kp.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase))
                        { hasParent = true; break; }
                    }
                    if (hasParent) continue;

                    bool exists = false;
                    try { exists = Reg.Exists(root, path); }
                    catch { }
                    if (exists) continue;   // 它已经回来了，不算"被禁用"

                    // 顺手把这一个键单独存成 .reg，方便以后精确恢复
                    string itemsDir = System.IO.Path.Combine(d, "items");
                    string leaf = LeafName(path);
                    string disp = leaf;

                    // 名字优先取这一节的默认值：普通 shell 键是 @="使用 ToDesk 快传文件"，
                    // CLSID 键是 @="ToDesk 快传"。从备份反推历史记录时，只有这样才能
                    // 显示成用户看得懂的名字，而不是 ToDeskSendFile 这种键名。
                    try
                    {
                        string defVal = ParseRegStringValue(sections[i], "").Trim();
                        bool literal = defVal.Length > 0
                                       && !defVal.StartsWith("{", StringComparison.Ordinal)
                                       && !defVal.StartsWith("@", StringComparison.Ordinal)
                                       && defVal.IndexOf(".dll,", StringComparison.OrdinalIgnoreCase) < 0
                                       && defVal.IndexOf(".exe,", StringComparison.OrdinalIgnoreCase) < 0;
                        if (literal)
                        {
                            disp = defVal;
                        }
                        else if (leaf.StartsWith("{"))          // CLSID 项：换成友好名
                        {
                            string fr = BuildDisplayName(leaf.ToUpperInvariant(), "");
                            if (!string.IsNullOrEmpty(fr)) disp = fr;
                        }
                    }
                    catch { }
                    string fn = "i" + (i + 1).ToString("00") + "_" + SafeFileName(leaf) + ".reg";
                    string itemPath = System.IO.Path.Combine(itemsDir, fn);
                    string rel = System.IO.Path.Combine(
                                     System.IO.Path.Combine("backups", stamp),
                                     System.IO.Path.Combine("items", fn));
                    bool wrote = false;
                    try
                    {
                        if (!Directory.Exists(itemsDir)) Directory.CreateDirectory(itemsDir);
                        File.WriteAllText(itemPath, sections[i], new UnicodeEncoding(false, true));
                        wrote = true;
                    }
                    catch { }

                    DisabledLog.Entry e = new DisabledLog.Entry();
                    e.Time = time;
                    e.Category = CategoryOfPath(path);
                    e.Name = disp;
                    e.Root = root;
                    e.Path = path;
                    e.Kind = (path.IndexOf("NameSpace", StringComparison.OrdinalIgnoreCase) >= 0)
                             ? "NameSpace" : "ShellKey";
                    // v3.0：从备份里把 Icon / command 也读出来，这样反推出的历史项
                    // 在列表里照样显示原软件的图标（而不是通用图标）
                    string iconVal = ParseRegStringValue(sections[i], "Icon");
                    string cmdVal = "";
                    for (int j = 0; j < keys.Count; j++)
                    {
                        if (string.Equals(keys[j], kp + "\\command", StringComparison.OrdinalIgnoreCase))
                        {
                            cmdVal = ParseRegStringValue(sections[j], "");
                            break;
                        }
                    }
                    try
                    {
                        e.IconSpec = PickSpecForShellKey(iconVal, cmdVal);
                        e.TargetFile = ResolveTarget(e.IconSpec, "", cmdVal);
                        e.AppName = AppNameOf(null, disp, e.IconSpec);
                    }
                    catch { }
                    e.ItemReg = wrote ? rel : "";
                    add.Add(e);
                    knownKeys.Add(root + "\\" + path);
                }
            }

            if (add.Count > 0)
            {
                DisabledLog.Append(exeDir, add);
                return add.Count;
            }
            return 0;
        }

        // 从一段 .reg 文本里取出指定值名的字符串值（支持 "Name"="..."、@="..."、hex(2)）
        private static string ParseRegStringValue(string section, string valueName)
        {
            if (string.IsNullOrEmpty(section)) return "";
            string prefix = (valueName.Length == 0) ? "@=" : "\"" + valueName + "\"=";
            string[] lines = section.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string t = raw.Trim();
                if (!t.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string v = t.Substring(prefix.Length).Trim();
                if (v.StartsWith("\""))
                {
                    int end = v.LastIndexOf('"');
                    if (end > 0) v = v.Substring(1, end - 1);
                    return v.Replace("\\\"", "\"").Replace("\\\\", "\\");
                }
                if (v.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string hex = v.Substring(6).Replace(",", "").Replace(" ", "");
                        if (hex.Length % 2 != 0) hex = "0" + hex;
                        byte[] b = new byte[hex.Length / 2];
                        for (int i = 0; i < b.Length; i++)
                            b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);
                        return Encoding.Unicode.GetString(b).TrimEnd('\0');
                    }
                    catch { return ""; }
                }
                if (v == "-") return "";
            }
            return "";
        }

        // .reg 文件按 [键] 切段：keys[i] 是键路径，sections[i] 是可以单独导入的 .reg 内容
        private static void SplitRegFile(string file, List<string> keys, List<string> sections)
        {
            string[] lines = File.ReadAllLines(file, Encoding.Unicode);
            StringBuilder cur = null;
            string curKey = null;

            foreach (string line in lines)
            {
                if (line == null) continue;
                string t = line.Trim();
                if (t.StartsWith("["))
                {
                    if (curKey != null) { keys.Add(curKey); sections.Add(cur.ToString()); }
                    int end = t.IndexOf(']');
                    curKey = (end > 1) ? t.Substring(1, end - 1) : t.Trim('[', ']');
                    cur = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
                    cur.Append(t).Append("\r\n");
                }
                else if (curKey != null)
                {
                    cur.Append(line).Append("\r\n");
                }
            }
            if (curKey != null) { keys.Add(curKey); sections.Add(cur.ToString()); }
        }

        private static string TopHive(string keyPath)
        {
            int i = keyPath.IndexOf('\\');
            return (i > 0) ? keyPath.Substring(0, i) : keyPath;
        }

        private static string LeafName(string path)
        {
            int i = path.LastIndexOf('\\');
            return (i >= 0 && i + 1 < path.Length) ? path.Substring(i + 1) : path;
        }

        private static string FormatStamp(string stamp)
        {
            try
            {
                DateTime t = DateTime.ParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                return t.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { return stamp; }
        }

        private static string SafeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "item";
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                bool ok = true;
                for (int i = 0; i < bad.Length; i++) if (c == bad[i]) { ok = false; break; }
                sb.Append(ok ? c : '_');
            }
            string r = sb.ToString().Trim();
            if (r.Length > 50) r = r.Substring(0, 50).Trim();
            return r.Length == 0 ? "item" : r;
        }

        // 从注册表路径猜它是"哪里的"右键菜单
        private static string CategoryOfPath(string path)
        {
            string p = path.ToLowerInvariant();
            if (p.IndexOf(@"\directory\background\shell\") >= 0) return "文件夹空白处";
            if (p.IndexOf(@"\allfilesystemobjects\shell\") >= 0) return "所有对象";
            if (p.IndexOf(@"\folder\shell\") >= 0) return "文件夹";
            if (p.IndexOf(@"\directory\shell\") >= 0) return "文件夹";
            if (p.IndexOf(@"\drive\shell\") >= 0) return "驱动器";
            if (p.IndexOf(@"\*\shell\") >= 0) return "文件";
            if (p.IndexOf(@"explorer\mycomputer\namespace") >= 0) return "此电脑图标";
            if (p.IndexOf(@"explorer\desktop\namespace") >= 0) return "桌面图标";
            if (p.IndexOf(ThisPcClsid.ToLowerInvariant()) >= 0) return "此电脑";
            if (p.IndexOf(RecycleClsid.ToLowerInvariant()) >= 0) return "回收站";
            return "（来自备份）";
        }

        // 把 disabled.tsv 里的历史记录补进扫描结果；
        // 某条记录对应的注册表键又回来了（用户自己恢复了）→ 自动清理该记录。
        // 返回：补了几条；cleaned 输出：清理掉几条。
        public static int MergeDisabledHistory(List<RightMenuItem> list, string exeDir, out int cleaned)
        {
            cleaned = 0;
            List<DisabledLog.Entry> entries;
            try { entries = DisabledLog.Load(exeDir); }
            catch { return 0; }

            List<DisabledLog.Entry> keep = new List<DisabledLog.Entry>();
            int added = 0;
            int insertAt = 0;   // 历史记录排在列表最前面：打开软件第一眼就该看到

            foreach (DisabledLog.Entry e in entries)
            {
                bool exists = false;
                try { exists = Reg.Exists(e.Root, e.Path); }
                catch { }

                if (exists) { cleaned++; continue; }
                keep.Add(e);

                // v3.0：老版本导入的记录没存图标规格，从单项备份 .reg 里补
                // （备份里有原始的 Icon / command 值）
                if (e.IconSpec.Length == 0 && e.ItemReg.Length > 0)
                {
                    try
                    {
                        string full = AppPaths.ResolveBackupPath(e.ItemReg);
                        if (full.Length > 0 && File.Exists(full))
                        {
                            List<string> ks = new List<string>();
                            List<string> ss = new List<string>();
                            SplitRegFile(full, ks, ss);
                            string iconVal = "", cmdVal = "";
                            for (int i = 0; i < ks.Count && i < ss.Count; i++)
                            {
                                string kk = ks[i].TrimEnd('\\');
                                if (kk.EndsWith("\\command", StringComparison.OrdinalIgnoreCase))
                                    cmdVal = ParseRegStringValue(ss[i], "");
                                else
                                    iconVal = ParseRegStringValue(ss[i], "Icon");
                            }
                            if (iconVal.Length > 0 || cmdVal.Length > 0)
                            {
                                e.IconSpec = PickSpecForShellKey(iconVal, cmdVal);
                                e.TargetFile = ResolveTarget(e.IconSpec, "", cmdVal);
                                if (e.AppName.Length == 0) e.AppName = AppNameOf(null, e.Name, e.IconSpec);
                            }
                        }
                    }
                    catch { }
                }

                RightMenuItem mi = new RightMenuItem();
                mi.Category = e.Category;
                mi.DisplayName = e.Name;
                mi.Root = e.Root;
                mi.Path = e.Path;
                mi.Clsid = e.Clsid;
                mi.IconSpec = e.IconSpec;
                mi.AppName = e.AppName;
                mi.TargetFile = e.TargetFile;
                mi.IsHistory = true;
                mi.DisabledAt = e.Time;
                mi.BackupItemReg = e.ItemReg;
                try { mi.Kind = (ItemKind)Enum.Parse(typeof(ItemKind), e.Kind); }
                catch { mi.Kind = ItemKind.ShellKey; }

                mi.Detail = "已于 " + e.Time + " 禁用　"
                          + (string.IsNullOrEmpty(e.ItemReg) ? "（无单项备份）" : ("备份：" + e.ItemReg));
                list.Insert(insertAt, mi);
                insertAt++;
                added++;
            }

            if (cleaned > 0) DisabledLog.Save(exeDir, keep);
            return added;
        }
    }

}
