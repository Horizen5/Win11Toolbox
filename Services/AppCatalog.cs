using System.Collections.Generic;

namespace Win11Toolbox.Services;

/// <summary>
/// 装机清单。
///
/// <para>
/// 表驱动：加一款软件就是加一行，和「轻松设置」那边一个思路。所有 ID 都用
/// <c>winget search --id X -e</c> 逐个验证过，不存在的不收 —— 目录里躺着一个
/// 装不上的条目，比少一个条目难受得多。
/// </para>
/// <para>
/// 只收「装了不会后悔」的：开源或官方渠道、有中文、长期维护。破解版、来路不明的
/// 一概不收，这个工具不做分发渠道。
/// </para>
/// </summary>
public static class AppCatalog
{
    public static List<InstallApp> Build() => new()
    {
        // ---------------- 浏览器
        new("浏览器", "Google Chrome", "Google.Chrome", "Chromium 内核，扩展生态最全"),
        new("浏览器", "Mozilla Firefox", "Mozilla.Firefox", "独立内核，隐私保护强"),
        new("浏览器", "Brave", "Brave.Brave", "内置广告与追踪拦截"),
        new("浏览器", "Vivaldi", "Vivaldi.Vivaldi", "可定制程度最高的 Chromium 浏览器"),

        // ---------------- 运行库
        new("运行库", "Visual C++ 运行库 x64", "Microsoft.VCRedist.2015+.x64", "大量软件和游戏的前置依赖"),
        new("运行库", "Visual C++ 运行库 x86", "Microsoft.VCRedist.2015+.x86", "32 位程序需要，和 x64 可以共存"),
        new("运行库", ".NET 桌面运行时 8", "Microsoft.DotNet.DesktopRuntime.8", "长期支持版，WPF / WinForms 程序需要"),
        new("运行库", ".NET 桌面运行时 9", "Microsoft.DotNet.DesktopRuntime.9", "标准支持版"),
        new("运行库", ".NET 桌面运行时 10", "Microsoft.DotNet.DesktopRuntime.10", "最新版，本工具箱自身也用它"),

        // ---------------- 开发
        new("开发", "Visual Studio Code", "Microsoft.VisualStudioCode", "轻量编辑器，插件生态最大"),
        new("开发", "Git", "Git.Git", "版本控制，顺带装上 Git Bash"),
        new("开发", "Windows Terminal", "Microsoft.WindowsTerminal", "多标签终端，比 conhost 好用太多"),
        new("开发", "PowerShell 7", "Microsoft.PowerShell", "跨平台新版 PowerShell"),
        new("开发", "Node.js LTS", "OpenJS.NodeJS.LTS", "前端与脚本运行时"),
        new("开发", "Python 3.12", "Python.Python.3.12", "自带 pip，装完记得勾选 PATH"),
        new("开发", "GitHub Desktop", "GitHub.GitHubDesktop", "不想敲命令时的图形化 Git"),
        new("开发", "Notepad++", "Notepad++.Notepad++", "大文件也能秒开的编辑器"),
        new("开发", "PowerToys", "Microsoft.PowerToys", "微软官方增强包：窗口布局、批量重命名等"),
        new("开发", "Postman", "Postman.Postman", "接口调试"),
        new("开发", "Docker Desktop", "Docker.DockerDesktop", "容器环境，会装 WSL2 后端"),
        new("开发", "JetBrains Toolbox", "JetBrains.Toolbox", "统一管理 IDEA / PyCharm 等"),
        new("开发", "WinMerge", "WinMerge.WinMerge", "文件和目录对比"),

        // ---------------- 办公
        new("办公", "LibreOffice", "TheDocumentFoundation.LibreOffice", "免费开源办公套件"),
        new("办公", "WPS Office", "Kingsoft.WPSOffice", "国内格式兼容好"),
        new("办公", "Notion", "Notion.Notion", "笔记与项目管理"),
        new("办公", "Obsidian", "Obsidian.Obsidian", "本地 Markdown 笔记，数据在自己手里"),
        new("办公", "Adobe Reader", "Adobe.Acrobat.Reader.64-bit", "PDF 阅读，官方免费版"),

        // ---------------- 媒体
        new("媒体", "VLC", "VideoLAN.VLC", "什么格式都能播"),
        new("媒体", "PotPlayer", "Daum.PotPlayer", "轻量播放器，硬件解码好"),
        new("媒体", "Spotify", "Spotify.Spotify", "音乐流媒体"),
        new("媒体", "OBS Studio", "OBSProject.OBSStudio", "录屏与直播"),
        new("媒体", "HandBrake", "HandBrake.HandBrake", "视频转码"),
        new("媒体", "Audacity", "Audacity.Audacity", "音频剪辑"),
        new("媒体", "Jellyfin Media Player", "Jellyfin.JellyfinMediaPlayer", "自建影音库的客户端"),

        // ---------------- 系统工具
        new("系统工具", "7-Zip", "7zip.7zip", "开源压缩，右键菜单干净"),
        new("系统工具", "WinRAR", "RARLab.WinRAR", "RAR 格式必须"),
        new("系统工具", "Everything", "voidtools.Everything", "秒级文件搜索，装了就离不开"),
        new("系统工具", "Rufus", "Rufus.Rufus", "做启动盘"),
        new("系统工具", "CrystalDiskInfo", "CrystalDewWorld.CrystalDiskInfo", "硬盘健康与温度"),
        new("系统工具", "CrystalDiskMark", "CrystalDewWorld.CrystalDiskMark", "硬盘读写测速"),
        new("系统工具", "CPU-Z", "CPUID.CPU-Z", "处理器与内存参数"),
        new("系统工具", "GPU-Z", "TechPowerUp.GPU-Z", "显卡参数与传感器"),
        new("系统工具", "ShareX", "ShareX.ShareX", "截图录屏加自动上传"),
        new("系统工具", "Sysinternals Suite", "Microsoft.Sysinternals.Suite", "微软官方排障工具合集"),

        // ---------------- 安全
        new("安全", "Bitwarden", "Bitwarden.Bitwarden", "开源密码管理器，支持自建"),
        new("安全", "KeePassXC", "KeePassXCTeam.KeePassXC", "纯本地密码库，不上云"),

        // ---------------- 通讯
        new("通讯", "微信", "Tencent.WeChat", "国内日常"),
        new("通讯", "QQ（NT 版）", "Tencent.QQ.NT", "新版 QQ，Electron 架构"),
        new("通讯", "Telegram", "Telegram.TelegramDesktop", "跨平台即时通讯"),
        new("通讯", "Discord", "Discord.Discord", "语音与社区"),
        new("通讯", "Zoom", "Zoom.Zoom", "视频会议"),
        new("通讯", "Slack", "SlackTechnologies.Slack", "团队协作"),

        // ---------------- 游戏
        new("游戏", "Steam", "Valve.Steam", "最大的 PC 游戏平台"),
        new("游戏", "Epic Games Launcher", "EpicGames.EpicGamesLauncher", "每周免费游戏"),
        new("游戏", "GOG Galaxy", "GOG.Galaxy", "无 DRM 游戏库"),

        // ---------------- 网络
        new("网络", "qBittorrent", "qBittorrent.qBittorrent", "开源 BT 客户端，无广告"),
        new("网络", "LocalSend", "LocalSend.LocalSend", "局域网跨设备传文件"),
        new("网络", "OneDrive", "Microsoft.OneDrive", "微软网盘同步客户端"),
    };
}
