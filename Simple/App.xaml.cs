using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Win11Toolbox.Services;
using Win11Toolbox.SettingsKit;

namespace Win11ToolboxSimple;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win11Toolbox", "simple-error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --selftest <日志路径>：装完机先跑一遍，确认这台机器上 65 项都能读、工具清单都在。
        // 不建窗口，跑完直接退出，退出码 0 = 全过。
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--selftest");
        if (index >= 0 && index + 1 < args.Length)
        {
            int code = RunSelfTest(args[index + 1]);
            Shutdown(code);
            return;
        }

        // 简洁版没有日志窗口，异常必须落盘，否则出问题只能靠猜
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            Write(eventArgs.Exception);
            MessageBox.Show("操作失败：" + eventArgs.Exception.Message + "\n\n详细信息已记录到：\n" + LogPath,
                "Win11 工具箱 · 简洁版", MessageBoxButton.OK, MessageBoxImage.Warning);
            eventArgs.Handled = true;
        };

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <summary>自检。返回 0 表示全部通过。</summary>
    private static int RunSelfTest(string outPath)
    {
        var lines = new List<string>();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; lines.Add("OK    " + name + (detail.Length > 0 ? "  " + detail : "")); }
            else { fail++; lines.Add("FAIL  " + name + (detail.Length > 0 ? "  " + detail : "")); }
        }

        try
        {
            lines.Add("管理员：" + (Win11Toolbox.Admin.IsAdmin() ? "是" : "否"));

            List<SettingItem> items = EasySettingsData.Build();
            Check("设置项数量", items.Count == 65, items.Count + " 项");

            int probeFail = 0;
            foreach (SettingItem item in items)
            {
                try { item.Probe(); }
                catch (Exception ex)
                {
                    probeFail++;
                    lines.Add("      探测失败：" + item.Name + " —— " + ex.Message);
                }
            }
            Check("全部设置项可探测", probeFail == 0, "失败 " + probeFail + " 项");

            int tools = ToolCatalog.Categories.SelectMany(c => c.Items).Count();
            Check("工具清单", tools == 47, tools + " 个");
            Check("快捷入口", EasySettingsData.QuickLinks().Length == 10,
                  EasySettingsData.QuickLinks().Length + " 个");

            // 往返测试：挑一项无副作用的（显示文件扩展名），开 → 关 各写一次，最后恢复原状
            SettingItem target = items.FirstOrDefault(i => i.Name == "显示文件扩展名");
            if (target == null)
            {
                Check("往返测试", false, "找不到「显示文件扩展名」");
            }
            else
            {
                bool original = target.Probe();
                target.Set(!original);
                bool after = target.Probe();
                target.Set(original);
                bool restored = target.Probe();

                Check("写入往返", after == !original && restored == original,
                      "写入后=" + after + "，恢复后=" + restored + "（原值 " + original + "）");
            }
        }
        catch (Exception ex)
        {
            fail++;
            lines.Add("FAIL  自检本身抛异常：" + ex);
        }

        lines.Add("");
        lines.Add($"结果：通过 {pass}，失败 {fail}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllLines(outPath, lines, new UTF8Encoding(false));
        }
        catch
        {
            // 日志写不进去也要给个退出码
        }

        return fail == 0 ? 0 : 1;
    }

    private static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var sb = new StringBuilder();
            sb.AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            File.AppendAllText(LogPath, sb.ToString());
        }
        catch
        {
            // 写不进去也不能再抛
        }
    }
}
