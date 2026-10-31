# 开发笔记

> 这份文档是**写给维护者的**：技术选型的取舍、踩过的坑、每个诡异现象的排查过程。
> 想了解这个工具能干什么、怎么用，看根目录的 [README](../README.md)。

# Win11 工具箱 · C# 版

用 **C# + WPF + WPF-UI** 重写的版本，替代原来的 Python/PySide 实现。
界面直接使用 Win11 的 Fluent 设计令牌与系统 Mica 云母背景，字体走 `Segoe UI Variable`，
不再有 Qt 自绘控件带来的"差一点"。

## 加新功能之前：按这个顺序找答案

1. **先看「Windows 11 轻松设置」是怎么做的。** 它已经反编译好，放在工作区
   `C:\Users\Administrator\WorkBuddy\2026-10-15-18-38-44\_re\src111`，
   原包是同目录的 `qssz111.zip`（V1.11，450 KB）。要加系统开关、应用装卸载、
   卸载 Edge、禁用 WD 这类功能，先去那里翻它写哪个键、用什么命令、按什么顺序。
2. **它没有覆盖到的，上网搜。** 优先级：MS Learn / 微软官方文档 → 系统行为的一手
   观察（注册表实测、进程监视）→ GitHub 上的同类开源实现
   （Win11Debloat、Optimizer、Sophia Script）。
3. **网上也查不到，自己做实验。** 在本机注册表上读写验证、重启资源管理器看效果，
   结论写回本文档对应章节。**不要再靠记忆写键值** —— 之前把
   `SearchboxTaskbarMode` 记到 `Explorer\Advanced` 下就是这类错误。

> **边界**：只看它**怎么实现**，不搬它的代码。它是闭源免费软件（作者「快乐无极」），
> 著作权归作者；本项目是 MIT 公开仓库，把反编译产物抄进来就是侵权。
> 注册表键值、系统命令、调用顺序属于事实，可以据此独立实现；
> 而且本项目每一项都要有 `Probe`（读回真实状态），它的实现根本没有这一层，
> 抄了反而是退步。

## 「Windows 11 轻松设置」V1.11 的实现要点

反编译环境：`ilspycmd` 11.2（`~/.dotnet/tools/ilspycmd.cmd`，不在 PATH 里）、
`dnSpyEx 6.6`（GUI 调试用）。程序集是 **.NET Framework 4.8.1 / WinForms / x64**，
无混淆无壳，ILSpy 一次过：16 个类 8083 行。

| 文件 | 行数 | 职责 |
| --- | --- | --- |
| `Form1.cs` | 3561 | 主窗口：软件目录、商店 App、资源管理器设置、禁用 WD |
| `Form4.cs` | 1390 | 其它设置：远程协助、UCPD、DEP、内存完整性 |
| `Form2.cs` | 1243 | 资源管理器 / 任务栏增强设置 |
| `Form3.cs` | 829 | 卸载 Edge |
| `Form5.cs` | 620 | 隐私设置 |
| `RegControl.cs` | 86 | 注册表读写中枢 |

### 提权：整程序 runas，不做按需提权

`Program.cs:24` 检测到不是管理员就 `Process.Start(Verb="runas")` 拿自己重启再退出。
所以它全程 High IL，写 `HKLM` 从不二次弹 UAC。代价是一打开就弹一次。
本项目相反：默认 Medium，只有真需要时才单独提权。

### 注册表：一层薄封装，且不回读

`RegControl.SetKeyValue` 就一行 `Registry.SetValue(key, subkey, value)`，失败弹框返回 -1。
**没有 Probe**：界面初始化时读一次值反推 checkbox，之后勾选直接按反向标志写死，
例如 `Form2.cs:361` 的 `SetKeyValueofInt(text, "HideFileExt", (!flag) ? 1 : 0)`。
系统自己改过设置后它就会显示错状态 —— 这是本项目必须保留 `Probe` 的理由。

### 禁用 Defender：三级递进，靠 TrustedInstaller 身份

`Form1.cs:1089-1140`。先检查篡改防护（`Features\TamperProtection == 5`）和实时保护，
没关就弹窗让用户手动关；然后逐级升级：

1. `MinSudo.exe --TrustedInstaller --Privileged DisableWD.bat` —— 随包分发的 MinSudo
   把批处理提到 **TrustedInstaller** 身份执行
2. 同身份 `rename MsMpEng.exe MsMpEng1.exe`（引擎文件改名，服务起不来）
3. `MpCmdRun.exe -DisableService` + `Set-MpPreference -DisableRealtimeMonitoring 1`，
   再轮询 `ServiceController.Status == Stopped` 确认

**可借鉴的点**：TrustedInstaller 令牌比 Administrator 高一层，是「系统锁死的键」
（如 `HKCU\...\CurrentVersion\Search`）唯一可能绕过去的路。代价是要分发第三方提权
exe，杀软必报。本项目暂不采用，但记在这里。

### 应用管理：三套接口并用

- 装：`winget install <pkg> -e --accept-source-agreements --accept-package-agreements`，
  失败回退 `start ms-windows-store://pdp?productid=<id>`（`Form1.cs:1312` / `1323`）
- 卸当前用户：`winget uninstall` + `Get-AppxPackage X | Remove-AppxPackage`（1346-1348）
- 卸预装：`DISM /Online /Remove-ProvisionedAppxPackage /PackageName:<全名>`（1336）——
  「预装」和「当前用户已装」是两件事，很多工具搞混
- 软件目录**表驱动**：`Software` 类只有 `SoftID / SoftName / RegKey / RegKey2 /
  SilentInstall / Description` 几个字段，加一款软件就是加一行数据

### 卸载 Edge：暴力删，不走卸载器

`Form3.cs:158-215`：`taskkill msedge` → `rd /S /Q` 三个目录 → 删
`StartMenuInternet`、`Uninstall`、`Classes\MSEdgeHTM / MSEdgePDF`、`App Paths`、
`Run` 自启项、`ApplicationAssociationToasts`。

**不建议照做**：系统更新会把 Edge 装回来，删注册表树还会留下半残状态。

### 刷新桌面/图标

`RefreshDesktop.cs`：`SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0)`，
与本项目的 `RefreshIcons` 是同一个 API。

### 结论：真正值得抄的是这三条

1. **DISM 卸预装应用** —— 「应用管理」页最该先做的一块，纯系统命令，零第三方依赖
2. **winget 集成 + 商店协议回退** —— 装软件不用自己维护下载地址
3. **TrustedInstaller 提权** —— 解决被锁注册表键的备选方案（有分发风险，先记着）

它的注册表开关本身覆盖度并不比本项目高，57 项里本项目已覆盖 49 项左右；
差距集中在**应用管理**这个模块上，不是开关数量。

## 为什么不是 WinUI 3

一开始按 WinUI 3（Windows App SDK）搭过工程，`dotnet build` 会在两个地方失败：

```
error MSB4062: 未能从程序集 ...\Microsoft\VisualStudio\v18.0\AppxPackage\
Microsoft.Build.Packaging.Pri.Tasks.dll 加载任务 ExpandPriContent
error MSB4062: ... Microsoft.Build.AppxPackage.dll 加载任务 RemovePayloadDuplicates
```

这两个任务（资源索引 PRI、Appx 打包）只随 Visual Studio 的 MSBuild 提供，
纯 .NET SDK 里没有；关掉 `AppxGeneratePriEnabled` 过了一个还会卡在下一个，
属于官方构建链强依赖 VS 的问题（要么装 5 GB 的 VS Build Tools）。

改用 **WPF + WPF-UI** 后，`dotnet build` 干净通过，同时保留了：
原生 Mica 背景、系统主题色、Segoe UI Variable 字体、Win11 控件外观（NavigationView / Card / TabView / ToggleSwitch / InfoBar）。
唯一差别是控件由 WPF-UI 复刻而非系统 WinUI 提供。

## 运行

```bash
# 直接跑发布好的自包含版本（不需要装 .NET）
publish_single\Win11工具箱.exe

# 或者源码运行（需要 .NET 10 SDK）
dotnet run -c Release
```

调试用的命令行参数：

```bash
Win11Toolbox.exe --page rightmenu    # home / hardware / fan / tools / utilities / optimize / settings / about
                                     # 另有 rightmenu / tray / easy
Win11Toolbox.exe --theme dark        # light / dark，用于截图与主题验证
Win11Toolbox.exe --selftest          # 自检：逐页切换 + 验证监控恢复，退出码 0/1，结果写 selftest.log

# 下面三个是纯后台模式：完全不建窗口，适合远程/不打扰地验证
Win11Toolbox.exe --probe             # 读一遍托盘清单 + 构造三个页面，结果写 probe.log
Win11Toolbox.exe --writetest         # 托盘开关写 1 → 读回 → 写 0 → 读回，结果写 writetest.log
Win11Toolbox.exe --shot tray out.png # 把某个页面离屏渲染成 PNG（窗口在屏幕外、不激活）
Win11Toolbox.exe --easytest [a.log]  # 导出 57 项系统设置的探测结果 + 23 条断言，退出码 0/1，默认写 easytest.log
Win11Toolbox.exe --scrolltest tray   # 离屏滚一遍并计时（加 --all 测全部项），结果写 scrolltest.log
Win11Toolbox.exe --navshot tray a.png # 用真实 NavigationView 离屏渲染（含左侧导航栏），结果写 a.png.log
Win11Toolbox.exe --navdump home a.txt # 打印焦点广告层 + 左侧导航视觉树（查焦点框、边框从哪来）
Win11Toolbox.exe --themedump a.txt    # 打印主题字典合并情况 + 关键画笔解析值 + 控件默认样式从哪来
Win11Toolbox.exe --rowdump rightmenu a.txt # 在真窗口上量列表行文字的 Foreground + 值来源（查"行里字是黑的"）
Win11Toolbox.exe --darkaudit a.txt    # 深色主题下逐页扫"偏暗文字"，按亮度算，防止黑字漏网
Win11Toolbox.exe --themeflash a.txt   # 切一次主题，拆解 Apply / NotifyThemeChanged / 各页的耗时，并断言内容区没蒙层
Win11Toolbox.exe --toggleanim a.txt   # 把 ToggleSwitch 模板里的动画定义挖出来（时长、缓动、目标属性）
Win11Toolbox.exe --switchtest a.txt   # 真的点一下深浅色开关，按 10ms 采样"主题何时变 / 蒙层何时出现"
Win11Toolbox.exe --dialogdump a.txt   # 量小对话框的上/下留白，顺带输出 a.png 预览图
Win11Toolbox.exe --powerdump a.txt    # 把 powercfg /list 读出的电源计划名落盘（验证中文名不乱码）
Win11Toolbox.exe --launchprobe a.txt  # 把 47 个工具的目标逐个解析，列出本机点不开的（如没装终端/画图）
Win11Toolbox.exe --startupicons a.txt # 逐个启动项验证 exe/lnk 解析 + 图标提取（查列表行图标）
Win11Toolbox.exe --fans a.txt         # 风扇/温度快照 + 硬件树（查 SuperIO 有没有被枚举出来）
Win11Toolbox.exe --fans --fanwrite=80 a.txt # 顺带真写一次 PWM：设 80% → 等 4 秒 → 读回 → 交回自动
Win11Toolbox.exe --adlfan 70 a.txt    # 绕过 LHM 直接调 ADL OverdriveN 写显卡风扇，并采样 5 秒转速
Win11Toolbox.exe --installpawnio a.txt # 走界面按钮同一条路径装 PawnIO（命令行版会等它跑完）
Win11Toolbox.exe --pxinfo a.png 300 28 a.txt # 把某一行/列的像素压成色块区间（量截图里到底画了什么线）
Win11Toolbox.exe --logtest out.txt    # 从后台线程调三次页面的 Log，结果写 out.txt（注意要带文件名）
Win11Toolbox.exe --monitorcost a.txt  # 量主页每秒采样各步骤的耗时（进程枚举 / 网卡 / 磁盘）
Win11Toolbox.exe --glyphsheet a.png   # 把 47 个工具图标画成一张总表（看谁的字形残缺/空白）
Win11Toolbox.exe --glyphaudit a.txt Drive Console  # 列出超 BMP 的字形 + 按关键词列可用候选
Win11Toolbox.exe --totp a.txt         # 用 RFC 6238 官方向量核对 2FA 验证码（Base32 + otpauth 解析）
Win11Toolbox.exe --ringsheet a.png    # 把倒计时圆圈 8 档进度排一行，核对绿→黄→红
Win11Toolbox.exe --navshot utilities a.png --uitab  # 截 2FA 标签页（自动塞两个演示账户）
Win11Toolbox.exe --uistall 18 a.txt   # 量 UI 线程卡顿：8ms 探针 + DWM 合成探针 + 窗口消息时间线
Win11Toolbox.exe --uistall 18 a.txt --withtaskmgr            # 自动开/关任务管理器，复现「关掉瞬间卡」
Win11Toolbox.exe --uistall 16 a.txt --withtaskmgr --killtaskmgr # 对照组：强杀而不是发 WM_CLOSE
```

`--shot` 还支持 `--pump N`：只等 N 毫秒就渲染，用来量「首屏空不空」。
默认是等 12 秒（右键菜单要扫注册表，得给足时间）。

`--selftest` 会依次打开 11 个页面（含风扇控制）、再回到主页与硬件页确认缓存页面正常，并在最后等 3 秒验证
「离开主页→回来」后监控计时器确实重新工作。改动后跑一遍就知道有没有把导航改坏。

> **注意**：`--selftest` 豁免单实例锁，应用开着也能跑。早期没豁免时，应用开着跑自检会**静默退出码 0、
> 却不写日志**——看起来像「通过」，其实是撞上单实例互斥直接退了（这个假阳性骗过我一次）。
> 所以看到 `exit 0` 还要确认 `selftest.log` 的**修改时间是刚才**，否则就是在读旧日志。

```bash
cd bin\Release\net10.0-windows\win-x64
.\Win11Toolbox.exe --selftest; echo $LASTEXITCODE; cat selftest.log
```

## 构建

```bash
dotnet build -c Release                              # 构建
dotnet publish -c Release -r win-x64 --self-contained true \
  -o publish_next -p:PublishSingleFile=false -p:EnableCompressionInSingleFile=false  # 文件夹版（启动最快）
dotnet publish -c Release -r win-x64 --self-contained true \
  -o publish_single -p:DebugType=none -m:1          # 单文件（就一个 exe，63 MB）
dotnet publish -c Release -r win-x64 --self-contained false \
  -o publish_lite -p:EnableCompressionInSingleFile=false -p:DebugType=none -m:1  # 精简单文件（12 MB，需装运行时）
```

`Win11Toolbox.csproj` 里已经开好单文件开关：

```xml
<PublishSingleFile>true</PublishSingleFile>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
```

产物是**一个 63 MB 的 exe**（.NET 运行时 + WPF + 原生库全打进去，压缩过）。
拷到任意 Win10/11 机器双击即可，不需要装 .NET，也不需要旁边的 DLL。
可以随便改名（比如 `Win11工具箱.exe`），单文件包不受文件名影响。

> **发布后必做一步：改名。** `publish` 永远输出 `<AssemblyName>.exe`，也就是 `Win11Toolbox.exe`；
> 而 `publish_single` 里那个中文名 `Win11工具箱.exe` 是**上一次发布留下的旧文件**，
> publish 不会动它。忘了 `mv` 的话，双击中文名跑的还是旧版本 —— 我踩过一次：
> 自检日志里少了新加的页面，还以为代码没进去。
>
> ```bash
> mv -f publish_single/Win11Toolbox.exe publish_single/Win11工具箱.exe
> ```
>
> 验证方法：`ls -l` 看两个 exe 的**修改时间**，只有一个、而且是刚才，才算对。

`publish_next` 增量发布会撞上 MSBuild `Copy` 任务的栈溢出（可稳定复现），
所以每次先把它挪走再发：`mv publish_next publish_next_old`（直接 `rm -rf` 会被安全策略拦下，
266 个文件属于批量删除）。

不压缩的话约 150 MB；压缩后启动多 0.17 秒（实测 1.97 s → 2.14 s），换来体积减半，值得。
首次运行会把原生库解压到 `%TEMP%\.net\` 下，之后启动不再解压。

> 注意：WPF 不支持 `PublishTrimmed`（裁剪），所以体积压不到几十 MB 以内；
> 想要更小只能改用 WinUI 3 或去掉自包含（要求目标机装 .NET 桌面运行时）。

## 项目结构

```
Win11ToolboxCS/
├─ Win11Toolbox.csproj
├─ App.xaml(.cs)                 WPF-UI 主题资源
├─ MainWindow.xaml(.cs)          FluentWindow + NavigationView + Mica
├─ Controls/
│  ├─ UiTheme.cs                 取色/取字体（全部走主题资源）
│  ├─ InfoRow.cs                 「标签 + 值」信息行（40px 行高 + 细分割线）
│  ├─ MetricCard.cs              首页监控卡
│  ├─ MetricBars.cs              自绘柱状历史（每秒一根，像素对齐）
│  ├─ ViewportLock.cs            锁住外层 ScrollViewer，让列表页拿到有界视口
│  ├─ UiThread.cs                后台线程 → UI 线程的日志/状态栏封送
│  ├─ IRefreshablePage.cs        页面自己实现「F5 刷新」「Ctrl+F 聚焦搜索」
│  ├─ ToolTile.cs                工具磁贴
│  └─ SpacingStackPanel.cs       WPF 缺少 Spacing 的补齐
├─ Services/
│  ├─ SystemInfo.cs              WMI 读取硬件信息
│  ├─ AppConfig.cs               主题 / 窗口位置 / 上次页面（%APPDATA%\Win11Toolbox\config.json）
│  ├─ AppInfo.cs                 版本号唯一来源（读 csproj 的 <Version>）
│  ├─ SingleInstance.cs          单实例互斥 + 把已在跑的窗口提到前台
│  ├─ SystemMonitor.cs           GetSystemTimes / GlobalMemoryStatusEx 每秒采样（在后台线程）
│  ├─ FanController.cs           风扇转速/温度读取 + PWM 写入（LibreHardwareMonitor）
│  ├─ AmdFan.cs                  AMD 显卡风扇，直接调 ADL OverdriveN（LHM 走的 OD5 已失效）
│  ├─ ToolCatalog.cs             47 个内置工具 + 启动 + 字形（SymbolRegular）
│  ├─ ShellTools.cs              启动项 / 回收站 / 临时文件 / 网络 / 哈希 / 电源计划
│  ├─ ShellIcon.cs               SHGetFileInfo 取系统图标（现仅作兜底）
│  ├─ NativeMethods.cs           P/Invoke
│  ├─ RightMenu/                 右键菜单管理（从「右键菜单管家」移植）
│  │  ├─ RightMenuCore.cs        注册表扫描 / 备份 / 恢复 / 禁用记录
│  │  ├─ RightMenuActions.cs     备份并禁用、恢复、导出、提权
│  │  └─ RightMenuIcons.cs       HICON → BitmapSource
│  ├─ Tray/
│  │  └─ TrayCore.cs             托盘图标清单（注册表）+ 运行状态
│  └─ SettingsKit/
│     └─ EasySettingsCore.cs     57 项安全系统开关的读写
└─ Pages/                        主页 / 硬件信息 / 风扇控制 / 系统工具 / 实用工具 / 系统优化 / 设置 / 关于
                                 + 右键菜单 / 托盘图标 / 轻松设置
```

## 功能对照（与 Python 版一致，另加风扇控制）

| 页面 | 内容 |
| --- | --- |
| 主页 | 设备卡（版式同「设置 → 系统」：设备图 + 名称 + 型号 + 重命名，下方两列规格）、CPU/内存/系统盘/网络的秒级柱状历史与数值、一键体检、8 个常用入口 |
| 硬件信息 | 8 个选项卡：概览 / 处理器 / 内存（逐条插槽）/ 显卡 / 主板与固件 / 存储 / 网络 / 其他 |
| 风扇控制 | **C# 版新增**（Python 版没有）：风扇曲线 + 手动调速 + 实时转速/温度，参考 Fan Control；主板侧依赖 PawnIO 驱动 |
| 系统工具 | 47 个 Windows 内置工具的分类磁贴，用 Segoe Fluent 字形（与系统设置同源）+ 关键词搜索 |
| 实用工具 | 文件哈希、Base64/URL 编解码、随机密码、2FA 验证码、端口检测、Ping |
| 系统优化 | 临时文件扫描与清理、回收站、DNS / 图标缓存 / 资源管理器、sfc、启动项列表（每项带软件图标，同任务管理器） |
| 设置 | 深色模式、跟随系统主题 |
| 关于 | 版本、功能与参考项目 |
| 右键菜单 | 124 项右键菜单扫描（含 12 项文件右键菜单），勾选后备份并禁用、随时恢复，可导出记录 |
| 托盘图标 | 36 项「其他系统托盘图标」清单，固定到任务栏 / 收进溢出区，支持撤销 |
| 轻松设置 | 57 项安全系统开关，9 个分类（资源管理器 / 右键菜单 / 任务栏 / 桌面 / 通知 / 开始菜单 / 系统性能 / Windows 更新 / 隐私），带需管理员 / 需重启角标；底部「电源计划」可在平衡 / 高性能 / 节能 / 卓越性能之间一键切换 |

## 三个移植模块的实现要点

**右键菜单**：直接读写 `HKCU/HKLM\Software\Classes\...\shellex\ContextMenuHandlers`，
禁用时先把整个键导出成 `.reg` 备份到 `%LOCALAPPDATA%\Win11Toolbox\backups`，再删键；
恢复就是把备份导回去。所有改动都会记到 `disabled.tsv`，软件里能看到「已禁用」的记录。

**托盘图标**：Windows 11 把「其他系统托盘图标」那份清单存在

```
HKCU\Control Panel\NotifyIconSettings\<哈希子键>
    ExecutablePath  REG_SZ    归属程序（可能以 {GUID}\ 开头，指某个已知文件夹，用 SHGetKnownFolderPath 还原）
    InitialTooltip  REG_SZ    悬停文字（经常是空的，为空时退回 exe 的「文件说明」）
    IsPromoted      REG_DWORD 1 = 始终显示在任务栏；缺失或 0 = 收进溢出区
```

系统设置页本身就是读写这里，所以直接改注册表即可，**不需要开设置页、不需要 UI Automation**。
早期版本用 UIA 去点设置页里的开关，既慢又依赖窗口标题，后台线程上还容易卡死；
现在读一份清单是毫秒级的，进页面就自动列出，改完立刻生效。
「是否在运行」用进程列表（exe 全路径 + 进程名）判断，不再去点开任务栏溢出区。

**轻松设置**：57 项都是可逆的 `HKCU/HKLM` 值或 `bcdedit`/`powercfg` 类调用，勾选即写、取消即还原，
高危项（会关掉安全功能或需要重启才生效的）已在移植时剔除或单独标角标。
页面底部另有一张「电源计划」卡：进页面时在后台线程跑一次 `powercfg /list` 列出全部计划
（平衡 / 高性能 / 节能 / 卓越性能），选中后 `powercfg /setactive` 立即切换 —— 就是控制面板
「电源选项」里能选的那几项，只是搬进了工具箱。

> **编码坑**：`powercfg` / `ipconfig` 这类控制台程序输出的是 **OEM 代码页**（简中系统为 936/GBK），
> 不是 UTF-8。早期用 `Encoding.UTF8` 读，计划名全变成 `0u♦♦♦♦♦`。而且 .NET Core 起默认**不自带** GBK，
> 直接 `Encoding.GetEncoding(936)` 会抛 `NotSupportedException`。现在引入
> `System.Text.Encoding.CodePages` 包并在 `ShellTools` 静态构造里注册
> `CodePagesEncodingProvider.Instance`，再按 `CultureInfo.CurrentCulture.TextInfo.OEMCodePage`
> 取编码，中文计划名才正常。`--powerdump` 就是专门验证这条链路的。

## 轻松设置扩到 57 项时踩到的四个坑

这批开关大部分是从 [Win11-Tweaks](https://github.com/Horizen5/Win11-Tweaks) 的 `g_ops[]` 表搬过来的
（39 个开关 → 51 个注册表值）。搬的过程里发现原实现和这边的旧代码各有几处不对，都记在这里：

**1. 桌面图标显隐写在 HKLM 是无效的。**
`HideDesktopIcons` 系统只读 `HKCU` 那一份，原来这 5 项写的是
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel`，
所以界面上勾得上、桌面上没反应，而且还要管理员权限才写得进去。现在改成 `HKCU`，
并且**两个子键都写**：`NewStartPanel` 管 Win11 开始菜单式桌面，`ClassicStartMenu` 管经典开始菜单式桌面，
只写一个的话在另一种桌面样式下会失效。老版本留在 HKLM 下的那些值是死的，不影响使用，想清掉得手动删。

**2. `Shell Icons\29` 和 `77` 是两件事。**
`29` = 快捷方式左下角的小箭头，`77` = UAC 盾牌角标。旧代码「去掉快捷方式小箭头」把两个一起写，
等于勾一项顺手改了另一项。现在拆成两项各管各的。

**3. `FFlags` 是位标志，不能整块覆盖。**
桌面图标的「自动排列」「对齐到网格」共用 `HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop\FFlags`
这一个 DWORD，同一位段里还塞着排序方式、图标间距等用户自己的偏好。整块写成
`1075839525` 会把它们一起冲掉。所以只能读-改-写、只翻目标位（`ApplyFlag`），
而且值不存在时才拿 1075839525 当基底。这段位运算单独在 `--easytest` 里钉了 5 条断言。

**4. 搜索框不在 `Explorer\Advanced` 下。**
`SearchboxTaskbarMode` / `SearchboxTaskbarModeCache` 在
`HKCU\Software\Microsoft\Windows\CurrentVersion\Search`。顺手还确认了一个反例：
`Explorer\Advanced\Sort = "Name"` 这个「桌面按名称排序」的写法查不到任何依据
（真正的 `Bags\1\Desktop\Sort` 是 `REG_BINARY` 的视图状态块，跟它不是一个东西），
大概率是「写了没人读」的安慰剂，所以**没有**收进工具箱 —— 宁缺勿假。

另外这批开关里有几个是「一组值共用一个开关」（关通知中心 = `DisableNotificationCenter`
+ `ToastEnabled` 两条一起写），用 `Multi(...)` 工厂统一处理：全部满足才算已开启，
勾选全写、取消全删，避免出现「只关了一半」的状态。

### 搜索框那一项：值写入被拦，靠「整键还原」绕过去

「隐藏任务栏搜索框」写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Search\SearchboxTaskbarMode`，
在这台机器上直接写报 `Attempted to perform an unauthorized operation.`（`0x80070005`）。

**先确认键和值是对的**：反编译的「Windows 11 轻松设置」用的就是同一个键 ——
`Form2.cs:186` 读、`Form2.cs:558` 写，写的时候走 `RegControl.SetKeyValueofInt`，
也就是一行 `Registry.SetValue`。所以不是键写错了。

再确认**拦的不是权限**：

| 试过的手段 | 结果 |
| --- | --- |
| `OpenSubKey(path, writable: true)` / `RegOpenKeyEx(KEY_SET_VALUE)` | 成功 |
| 同一个句柄上 `CreateSubKey` | **成功** |
| 同一个句柄上 `DeleteValue` | **成功** |
| 同一个句柄上 `SetValue` | 拒绝 |
| 以管理员跑（High IL）、以 **SYSTEM** 跑 | 都拒绝 |
| 用 `FullControl` 打开（`RegistryRights.FullControl`） | 打开成功，`SetValue` 照样拒绝 |
| 临时接管所有权 + 补 `KEY_ALL_ACCESS` 的 ACE，再写 | 拒绝（`RegOpenKeyEx` 已通过说明 DACL 不是瓶颈） |
| 看 SACL / 完整性标签 | 没有 `S:` 段，不存在标签差异 |
| 换 `RegCopyTree` 从暂存键拷进去 | 拒绝 |

删值要 `KEY_SET_VALUE`、写值也要 `KEY_SET_VALUE`，前者成功后者失败 —— 同一个句柄、
同一个权限位，所以**不可能是访问检查**，是「往这个键写值」这个动作本身被拦。
`CurrentVersion\SearchSettings` 同样如此。

**走得通的是整键还原**（`MutateProtectedKey`）：

```
RegSaveKey 存盘 → RegLoadKey 挂成 HKU\<临时名> → 在副本上随便改（副本不受保护）
→ RegUnLoadKey → RegRestoreKey(REG_FORCE_RESTORE) 把整键换回去
```

三个必须踩过的坑：

1. **`RegLoadKey` 不接受 `HKCU`**，只能挂到 `HKLM` / `HKU`，所以目标是 `HKU\<临时名>`。
2. **`RegRestoreKey` 不带 `REG_FORCE_RESTORE` 会失败**：键正被搜索服务占着，返回拒绝访问。
3. **`RegSaveKey` 不保存安全描述符**。还原之后这个键的 DACL 会变成默认值，
   原来那条只给搜索能力的 ACE（`S-1-15-3-1024-...`）就没了。
   所以还原前先抄一份 SDDL，还原后**接管所有权再把原 SDDL 写回去**。
   `--easytest` 里钉了一条断言专门看这个（比对「有效 ACE 集合」，不比对整串 SDDL ——
   系统会把继承 ACE 重新铺一遍，多出 `AI` 标记和 inherit-only 条目，那是它自己的动作）。

**还有一个坑：不重启资源管理器就不生效。**

整键还原把外壳挂在这个键上的 `RegNotifyChangeKeyValue` 弄断了。实测：

| 做法 | 隐藏（0） | 显示（2） |
| --- | --- | --- |
| 只广播 `WM_SETTINGCHANGE("TraySettings")` | 生效 | **不生效**（删得掉、建不回来） |
| 在键上建一个临时子键（触发注册表变更通知） | 生效 | **生效** |

所以 `KickShell()` 会建一个 `__wbrefresh__` 子键把通知发出去。这个子键**删不掉**
（同样是写保护，建得出来、删不掉），但它会在下次切换时被 `SweepTempSubKeys`
连同整键还原一起清掉 —— 任何时刻最多留一个空子键，不影响系统。

顺带一个副作用：`Search\Launch` 是**易失键**，`RegSaveKey` 存不下来，
还原之后它会消失，搜索用的时候由系统自己重建。

> 排查经验两条：
> 1. **判断一个键能不能写，别看 DACL，要实测**（`CanWriteKey()`）。DACL 允许但写入被拒是真实存在的。
> 2. **改完不生效，先分清是「值没写进去」还是「写进去了没人读」**。
>    这次两个都占了，而且「删得掉建不回来」这种不对称现象就是区分二者的线索。

### `--easytest` 曾经是个假阳性

`--easytest` 被单实例锁挡在外面：用户开着工具箱时跑它，`SingleInstance.TryAcquire()` 返回 false，
`OnStartup` 直接 `Shutdown()` —— **退出码 0、日志没写，看起来像通过，其实一行都没跑**。
`--selftest` 早就因为踩过同一个坑而豁免了实例锁（代码里那段注释就是当时写的），
`--easytest` 后来加上时漏了。现在两者都在 `isSelfTest` 白名单里。

> 教训：任何「跑完写个日志、退出码表示成败」的自检，都要先确认它**真的跑到了**。
> 顺手验证的办法是故意让它失败一次（`Check(false, ...)`），确认退出码是 1 而不是 0。

**工具磁贴的图标**：一开始用 `ShellIcon.FromPath` 从工具 exe 里抠图标，结果注册表编辑器、事件查看器、
设备管理器这些 `.msc` 全都指向同一个 `mmc.exe`，抠出来不是重复就是空白、甚至不相干。后来改成直接写
`SymbolRegular` 字形（Segoe Fluent Icons，和系统「设置」用的是同一套图标语汇），每个工具一个语义明确的字形
（注册表=Key24、设备管理器=Board24、电源=Power24……）。`ToolCatalog.MissingGlyphs()` 会在 `--selftest` 里
断言「47 个工具一个都没漏配」，漏配只会显示空白图标、不会报错，所以必须主动查。

#### 字形码位超出 BMP 就画不出来（踩过一次）

`SymbolRegular` 的枚举值就是码位，但**名字存在 ≠ 能画出来**。字体是 16 位 cmap，
码位一旦超过 `U+FFFF`，`SymbolText` 里就查不到字形，渲染出来是一个**残缺的角标**（看着像 `⌐`、`⌄`），
不抛异常、不报错、日志里也没有任何痕迹。

实测踩到的四个（用户截图报的就是其中两个）：

| 工具 | 原字形 | 码位 | 现在改成 |
|---|---|---|---|
| 服务和应用程序 | `SettingsCogMultiple24` | `U+F013D` | `Settings24` (U+F6AA) |
| 磁盘管理 | `HardDrive24` | `U+F0306` | `HardDrive20` (U+E6E3) |
| 命令提示符 | `Prompt24` | `U+F0631` | `WindowConsole20` (U+EE6F) |
| 查看 IP 配置 | `Prompt24` | `U+F0631` | `PlugConnected24` (U+E99A) |

规律：**4 位十六进制的都没问题，5 位的全是坏的**。所以现在 `MissingGlyphs()` 除了查「配没配」，
还会把 `code > 0xFFFF` 的报出来，`--selftest` 的 `tool-glyphs` 一项会直接 FAIL，不会再漏。

> 两个诊断配合用：
> - `--glyphsheet a.png` —— 用界面同一条渲染路径把 47 个图标画成一张总表，**用眼睛看谁坏了**，最可靠；
> - `--glyphaudit a.txt [关键词…]` —— 列出超 BMP 的，并按关键词列出可用候选（如 `--glyphaudit a.txt Drive`）。
>
> 别用 `GlyphTypeface.CharacterToGlyphMap` 当判据：实测这个字体读出来的 map 不全，
> 会把明明能正常显示的 4 位码位也报成「缺失」（第一次就踩了，报了 38 个假阳性，和截图完全对不上）。

**工具缺失时的兜底**：不是每台机器都装了全套 Windows 组件。`wt.exe`（Windows 终端）是
App Execution Alias，`mspaint.exe`（画图）也可能是 Appx。以前直接 `Process.Start("wt.exe")`，
找不到就抛一句英文 `An error occurred trying to start process 'wt.exe'…`，用户一脸问号。

现在 `ToolCatalog.Launch` 统一处理：

- 先按「App 别名（`%LOCALAPPDATA%\Microsoft\WindowsApps`）→ PATH → System32」解析出真实路径；
- 协议/URL（`ms-settings:`、`shell:`、`ms-screenclip:`、`https://…`）直接交给 ShellExecute；
- **Windows 终端**没装 → 退回「命令提示符」，并弹提示说明原因和安装方式；
- 其它 exe 缺失（如本机没装画图）→ 返回友好提示「这台电脑没有安装「画图」」，不再抛英文报错。

`--launchprobe` 会把 47 个工具全解析一遍，列出本机不可用的那几个（本机实测：`wt.exe`、`mspaint.exe`）。

**启动项图标**：系统优化页的启动项列表，每行名称前显示对应软件的 16px 图标（和任务管理器「启动」页一致）。
从 `StartupEntry.Command` 里解析出 exe 路径（处理带引号 / 带参数 / 裸路径三种写法），启动文件夹里的项直接用
`.lnk` 路径；再 `ShellIcon.FromPath(path, small: true)`（`SHGFI_SMALLICON`）取系统小图标 —— 比把 32px 大图缩下去清晰。
顺手过滤掉启动文件夹里的 `desktop.ini` 和隐藏 / 系统文件（之前它被当成一个叫「desktop」的启动项列出来）。
`--startupicons` 可以逐个验证：本机 13 项全部解析成功并取到图标。

## 布局：页面高度与虚拟化（踩过的坑）

WPF-UI 的 `NavigationView` 在内容外面套了一层 `ScrollViewer`，这一层会用**无限高度**测量页面。
列表页要是拿不到确定高度，`ListBox` 就永远不虚拟化，整页变成一个超长滚动条。

早先的做法是给页面根元素绑 `Height = ScrollViewer.ViewportHeight`。但这样又踩了第二个坑：
显式高度（`ViewportHeight`）叠加根元素的上下 `Margin`（18 + 16）会**多出 34px**；
而 WPF 里「显式 Height + `VerticalAlignment=Stretch`」等价于**居中**，
于是超出的 34px 上下各溢出一半 —— 表现就是页面顶部标题被切掉一条（约 17px）。

现在的做法是 `Controls/ViewportLock.cs`：页面 `Loaded` 时把外层 `ScrollViewer` 的
`Vertical/HorizontalScrollBarVisibility` 设为 `Disabled`，`Unloaded` 时用 `ConditionalWeakTable`
记下的原值恢复。滚动关掉后外层按可视区大小测量内容，页面自然撑满、列表拿到边界，
既不用硬编码高度，宽度也不会缩成内容宽（star 列才能正确分配）。

验证方式：`--navshot` 用真实 `NavigationView` 离屏渲染并打印各层 ScrollViewer 的视口/内容/可滚高度。
正常情况下外层应该是「视口 = 内容、可滚 = 0」，列表那层才有滚动量：

```
SV DynamicScrollViewer ''            视口=771.0x961.0 内容=771.0x951.6 可滚=0.0x0.0   H=Disabled V=Disabled
SV ScrollViewer ''                   视口=487.3x901.0 内容=1080.0x891.6 可滚=592.7x0.0 H=Disabled V=Auto
页面：TrayIconsPage 实际 961.0x771.0
```

`--navshot` 还会先「切到主页再切回来」，确认离开页面时外层滚动确实恢复了、回来又重新锁上。

### 主页是另一回事：它自己有一层 ScrollViewer

主页内容（设备卡 + 四张监控卡 + 体检 + 常用入口）比一屏高，页面根元素本来就是
`<ScrollViewer VerticalScrollBarVisibility="Auto">`。但这层一样会被外层无限高量成
「内容多高它就多高」，`ScrollableHeight` 永远是 0 —— 滚轮被它吃掉，页面看着能滚实际不动。

所以主页也挂上了同一把锁。锁完之后：

```
SV DynamicScrollViewer ''   视口=771.0x961.0 内容=771.0x850.0 可滚=0.0x0.0   V=Disabled   ← 外层锁住
SV ScrollViewer ''          视口=717.0x913.0 内容=1011.6x802.0 可滚=294.6x0.0 V=Auto      ← 页面自己滚
```

`WheelScroller` 是从事件源往上找第一个 `ScrollableHeight > 0` 的 ScrollViewer，
所以锁住外层之后滚轮自然落到页面这层，不用改滚轮代码。

另外列表页的表格列改成了星号分配（`2*` / `3*` + `MinWidth`），窗口拉到最小宽度时列会按比例收窄
而不是溢出到看不见；底部操作栏用 `WrapPanel`，窄窗口下按钮自动换行，不会把右侧状态文字挤掉。

### 列表卡片必须显式写 Stretch

`ui:Card` 的模板把内容垂直居中（`VerticalContentAlignment` 默认不是 `Stretch`）。
列表满屏时看不出来；一旦搜索把结果筛到只剩两三行，卡片就缩成一条、浮在整行的**正中间**，
上下各留一大片空白——看起来像页面布局塌了。

所以三个列表页的卡片都显式写了：

```xml
<ui:Card Grid.Row="2" Padding="0" VerticalAlignment="Stretch" VerticalContentAlignment="Stretch">
    <Grid VerticalAlignment="Stretch">
```

复现与验证要用 `--navfilter` 把结果筛小，否则 `--navshot` 拍出来永远是对的：

```
Win11Toolbox.exe --navshot rightmenu a.png --wait 3500 --navfilter wiztree
```

### 小对话框底部一大块空白：`FluentWindow` 的 `MinHeight = 320`

`InputDialog`（重命名那类单行输入框）用 `SizeToContent = Height` 自动撑高，
但内容只有 185px 高，窗口却始终是 320px —— 多出来的 135px 全堆在「下一步」下面，
上留白 24、下留白 159，差了 6 倍。

原因是 WPF-UI 的 `FluentWindow` 默认样式里写死了 **`MinHeight = 320`**。
`SizeToContent` 撑不过最小高度，把 `Height` 直接设成 185 也会被夹回 320：

```
窗口 MinHeight=320.0  DesiredSize=320.0
强制 Height=185.0 之后：ActualHeight=320.0（被最小高度卡住）
```

一行 `MinHeight = 0` 解决：

```
                窗口高   上留白   下留白
修之前           320      24      159
修之后           185      24       24
```

`--dialogdump` 会把这个对话框离屏搭一遍，逐行打印每个子元素的「距窗口顶 / 距窗口底」，
并顺手输出一张 PNG（`--dialogdump a.txt` → `a.txt` + `a.png`），不用靠看截图猜。

## 首页柱状历史（`Controls/MetricBars.cs`）

原来是「折线 + 渐变填充」，问题很集中：折线每秒一个采样点，硬拐角把 CPU 抖动连成锯齿；
1.6px 的线跨两个物理像素，抗锯齿摊成半透明灰；填充 15% 不透明度基本看不见；
而且满刻度写死 100，CPU 跑 15% 时曲线只有 5px 起伏，等于没有信息。

现在改成 **macOS 活动监视器那种每秒一根的柱状历史**（36 秒窗口）：

- **像素对齐**：柱子的左右边界按 `VisualTreeHelper.GetDpi()` 取整到物理像素，
  小尺寸下不会糊成一片；间隙随柱宽自适应（≥5px 时留 1.5px，否则 0.9 / 0.4）。
- **量程分两种**：
  - CPU 用**自适应**（满刻度 = 峰值 × 1.18，下限 30，涨得快落得慢）——负载忽高忽低，
    固定 0–100 时低负载就是一条贴底的线。
  - 内存 / 磁盘用**固定 0–100**——它们是「绝对占比」，自适应会把 43% 放大成满格，看着像要爆了。
  - 网络是速率，绝对数值本身没意义，用自适应（下限 0.1 MB/s）。
- **最新一根满色、历史 75% 不透明度**，一眼知道「现在」在哪根。
- **阈值变色**：CPU/内存/磁盘 ≥70% 转警示色、≥85% 转严重色（走 `SystemFillColorCautionBrush` /
  `SystemFillColorCriticalBrush`，深浅主题自动跟随）。
- 窗口没填满时柱子**从右往左长**，不拉伸 x 轴，避免刚启动那半分钟形状和稳定后不一样。
- 柱子最小 2px 高：网络空闲时数值接近 0，1px 的柱子在肉眼看来等于「没数据」。

每张卡自己画背景 + 1px 描边 + 8px 圆角（直接继承 `Border`，不用 WPF-UI 的 `Card`——
`Card` 默认背景近乎透明，四张并排会连成一整条），卡与卡之间隔 12px；
左上角一枚 24px 的淡色胶囊装图标，作为每张卡的点缀。

## 导航栏的焦点框

点一下左侧导航栏的空白处，`PART_ScrollViewer` 会拿到键盘焦点，WPF 给它画一个 1px 深色圆角框
（`#3D3E40`，还带一层淡填充），把整列导航项框起来 —— 看着像「这几个被圈在一起了」。

`MainWindow.RemoveScrollFocusVisuals` 在 `Nav.Loaded` 和窗口 `Loaded` 各跑一次：
把导航区所有 `ScrollViewer` 的 `FocusVisualStyle` 置空，并把左侧那层设为 `Focusable = false`
（根本拿不到焦点，点空白处也不会再有框）。导航项自己的键盘焦点框保留，不影响键盘操作。

验证：`--navdump home a.txt` 里 `PART_ScrollViewer` 那一行改前是
`[4,108 274x619] focusVisual=有 focusable`，改后只剩 `[4,108 274x619]`。

## 跨线程：后台操作怎么回 UI 线程

三个列表页（右键菜单 / 托盘 / 轻松设置）的按钮都是 `await Task.Run(...)`：读注册表、写注册表、
重启资源管理器这些活扔到线程池，界面不卡。问题出在**回调**上 —— 后台线程里直接写
`StatusLine.Text` 会抛：

```
System.InvalidOperationException: 调用线程无法访问此对象，因为另一个线程拥有该对象。
   at System.Windows.Controls.TextBlock.set_Text(String value)
   at Win11Toolbox.Pages.RightMenuPage.Log(String message)
```

`Controls/UiThread.cs` 就一行逻辑：当前在 UI 线程就直接执行，否则 `Dispatcher.Invoke`。
三个页面的 `Log` 统一走它，`Apply` / `Restore` / `RestoreOneHistory` 传进去的 `Action<string>`
就不用各自操心线程了。

```csharp
internal static class UiThread
{
    public static void Run(DispatcherObject owner, Action action)
    {
        var dispatcher = owner.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    // 写日志/刷状态栏专用：出错只记 error.log，不往调用方抛
    public static void RunSafe(DispatcherObject owner, Action action)
    {
        try { Run(owner, action); }
        catch (Exception ex) { App.Log(ex); }
    }
}
```

两个细节：

- **owner 传要改的那个控件**（`UiThread.RunSafe(StatusLine, ...)`），不要传页面。
  `DispatcherObject` 的 Dispatcher 是构造时绑定的，传控件等于「要改谁就用谁的线程」。
- **`Log` 走 `RunSafe`**。`Apply_Click` 是 `async void`，日志写入如果抛异常会被
  `Task.ThrowAsync` 抛到 `DispatcherUnhandledException`，弹一个「操作失败」的框 ——
  可实际上注册表已经改完了，只是状态栏没写上去。现在这种情况只落日志、不弹窗。

调用方全是 `await Task.Run(...)`，UI 线程没有被阻塞，所以 `Invoke`（同步）不会死锁 —— 换成
`BeginInvoke` 反而会让日志顺序乱掉。真正要小心的是**诊断模式**：`--logtest` 如果写成
`task.Wait()`，UI 线程就卡住等后台线程，后台线程又在等 UI 线程的 `Invoke`，直接互锁。
所以那里用的是一个手动的 `while (!task.IsCompleted && 已用时间 < 8s) Pump(50);` 抽水循环。

同一类坑还有一处不在页面上：`ShellTools.CheckPortAsync` 用 `Task.WhenAny(connect, Delay)`
做超时，超时后 `connect` 就没人 await 了，稍后它以 `SocketException(10061)` 失败，
变成 `TaskScheduler.UnobservedTaskException` 被写进 `error.log` —— 看着像崩溃，其实只是
「那个端口没开」。现在超时分支挂一个 `ContinueWith` 把异常吃掉，日志干净了。

## 使用体验（v1.1.0）

### 单实例

双击两次不再开两个窗口。`Services/SingleInstance.cs` 用 `Local\Win11Toolbox.SingleInstance`
互斥体判重；已经在跑的那个把自己的 PID 写到 `%APPDATA%\Win11Toolbox\instance.pid`，
后来者读这个 PID 找到窗口，`ShowWindow(SW_RESTORE)` + `SetForegroundWindow` 提到前台，然后自己退出。

为什么不按进程名找窗口：exe 可以随便改名（`Win11工具箱.exe`），按名字会找不到。
提权用 `AttachThreadInput` 绕开前台锁，否则后台进程调 `SetForegroundWindow` 只会让任务栏图标闪一下。

所有 `--probe` / `--shot` 这类诊断模式都在单实例判断**之前** return，所以可以和应用同时开着跑。

### 记住上次的样子

`%APPDATA%\Win11Toolbox\config.json`（和旧 Python 版同一个文件，键名改成 camelCase）：

```json
{ "theme": "auto", "width": 1240, "height": 820, "left": 660, "top": 286,
  "maximized": false, "lastPage": "rightmenu" }
```

- 主题：`--theme` 参数 > 配置 > 跟随系统
- 窗口：关闭时存大小与位置；最大化时存 `RestoreBounds`（直接读 `Width` 会记成全屏大小）
- 位置恢复前会检查是否落在可见屏幕内（拔掉副屏后窗口不会开到屏幕外）
- **启动一律回主页**。早先做过"记住上次页面"，实测反而别扭：一开机就落在右键菜单页，
  没有"我现在在哪"的锚点。`lastPage` 仍写进配置，只作排查用；`--page` 参数依旧优先。

配置文件坏了、被占用、没权限都只是退回默认值，绝不影响启动。

### 被禁用的项怎么找回来

右键菜单禁用之后，那一条就不再出现在注册表扫描结果里了，所以要靠三路来源把它"捞回来"：

| 来源 | 覆盖什么 | 丢了会怎样 |
| --- | --- | --- |
| `disabled.tsv`（exe 同目录） | 每次禁用的明细：时间/类别/名称/根/路径/CLSID/图标/软件名/单项备份 | 还能靠下面两路补 |
| `backups\<时间戳>\restore.reg` | 每批备份的完整 .reg。扫的时候看"备份里有、注册表里没有"的键 | 反推不出来，只能手动导入 .reg |
| 系统 `Shell Extensions\Blocked` 键 | 外壳扩展类屏蔽项（不是删键，是加屏蔽值） | 状态显示不出来 |

关键顺序：`Apply()` 是**先写 `disabled.tsv` 再删注册表键**，所以哪怕中途崩了，记录也已经在盘上，
下次启动反推得回来。`ImportHistoryFromBackups` 会遍历 `backups\*`，把 `restore.reg` 里
"注册表已不存在"的键补成「已禁用」条目，并把每一节单独写成 `items\iNN_*.reg` 供精确还原。

实测（把 exe 和 `backups` 复制到空目录、**不带** `disabled.tsv`）：照样列出两条 ToDesk 记录，
点「恢复选中项」能还原 —— 说明记录不是只依赖那个 tsv。

从备份反推时，菜单名优先取注册表默认值（`@="使用 ToDesk 快传文件"`），拿不到才退回键名，
否则列表里会出现一堆 `ToDeskSendFile` 这种看不懂的东西。

另外 `Apply_Click` 用 `try/finally` 包住：**不管成功还是抛异常都重扫一遍列表**。
之前出过一次「禁用了但列表里找不到」——就是异常把后面的 `await RescanAsync()` 跳过了，
界面还停在旧状态。

> 注意：`disabled.tsv` 和 `backups` 都在 **exe 所在目录**。把 exe 单独复制到别处运行，
> 那一份就是"全新"的，看不到原来的记录。要搬就整个目录一起搬。


### 危险项要确认，写完要回读

「轻松设置」里 6 项被标成**谨慎**（行首有红色角标，悬停能看到具体后果）：
关闭系统还原、关闭休眠、关 SysMain、关内存压缩、暂停更新到 2042、防火墙拦搜索联网。
勾选前弹确认框；取消勾选（= 恢复默认）不打扰。

每次写入后都会再 `Probe()` 一次做**回读校验**，三种结果分开处理：

| 情况 | 界面表现 |
| --- | --- |
| 读回来和目标一致 | 绿色「已开启」/ 灰色「默认状态」 |
| 想关但读回来还开着 | 橙色「待重启生效」+ 提示条，勾选保留 |
| 想开但读回来还是关的 | 红色「未生效」，**勾选自动弹回去**，提示条说明多半是缺管理员权限 |

以前是写完就把开关留在用户点的那一侧，非管理员运行时等于在骗人。

顺带修的两个真 bug：
- 暂停更新把 `PauseUpdatesStartTime` 和 `ExpiryTime` 写成同一个值，Windows 会判定无效 —— 现在开始时间是「现在」。
- 探测只看 `FlightSettingsMaxPauseDays` 存不存在（系统自己也会写），改成看 `PauseUpdatesExpiryTime` 是否真的在未来。
- 关闭内存压缩原来不管勾上还是取消都执行 `Disable-MMAgent`，取消永远恢复不了 —— 现在按方向分 `Enable/Disable`。

### 快捷键

| 键 | 作用 |
| --- | --- |
| `F5` | 重新读当前页数据（右键菜单=重扫注册表，托盘=重读清单，轻松设置=重探状态） |
| `Ctrl + F` | 跳到当前页搜索框（右键菜单 / 系统工具 / 轻松设置） |
| `Ctrl + 1..9` | 按左侧导航顺序切页 |

页面只要实现 `IRefreshablePage`（两个方法）就自动支持，主窗口不用按类型 switch。
`NavigationView` 没有暴露 `Content`，所以当前页是在视觉树里找第一个 `IsVisible` 的 `Page`
（缓存页还在内存里但不可见，不会被误选）；为了不在每次按键都走一遍树，先判键再做查找。

### 版本号只有一个来源

csproj 里 `<Version>1.1.0</Version>`，`Services/AppInfo.cs` 读程序集的
`InformationalVersion`（去掉 `+commit` 后缀），设置页和关于页都从它取。以前两处硬编码 `1.0.0`。

### 启动模式判定

原来靠 `Directory.Exists("C:\Windows\Boot\EFI")` 猜 UEFI，系统装在 D 盘或 EFI 分区没挂到那个路径就误判。
改用 kernel32 的 `GetFirmwareType`（Win8 起的官方接口），失败才回退到目录检查 + `%SystemRoot%`。

## 滚动性能

列表页（右键菜单 124 项 / 托盘 36 项 / 轻松设置 57 项）最初是 `ItemsControl` 套 `ScrollViewer`，
**没有虚拟化**——所有行一次性实例化，滚动时每帧都要重画整棵树。现在统一换成虚拟化的 `ListBox`
（`VirtualizingStackPanel` + `VirtualizationMode=Recycling` + `ScrollUnit=Pixel`，容器模板只剩一个
`ContentPresenter`，不带任何选中态样式）。

用 `--scrolltest` 在离屏窗口里逐帧滚动计时（软件渲染，比真实 GPU 渲染更严格）：

| 页面 | 项数 | 实例化容器 | 优化前 | 优化后 |
| --- | --- | --- | --- | --- |
| 右键菜单 | 31（默认筛选） | 17 | 11.4 ms | 10.7 ms |
| 右键菜单 | 124（全部项） | 17 | 约 40 ms（按线性推算） | 13.1 ms |
| 托盘图标 | 36 | 19 | 11.6 ms | 11.0 ms |
| 轻松设置 | 57 | 11 | 16.1 ms | 9.5 ms |

顺带修的两处：
- 行的状态色不能在属性 getter 里 `new SolidColorBrush`：滚动时每帧都产生新画刷，绑定认为值变了再重画一次。
  也不能写成 `static readonly` —— 静态字段会在类型初始化时把当时那套主题的颜色"焊死"，切了深浅色就不动了。
  现在统一走 `UiTheme.Success/Caution/Critical/Neutral`，每次读都从主题字典里取（返回的是同一个实例，
  不会产生新对象），主题切换由 `UiTheme.ThemeChanged` 通知各页把列表重绑一遍。
- 行图标 18px 却用 `BitmapScalingMode=HighQuality`（Fant 滤镜），改成 `LowQuality`（双线性），
  小图上肉眼无差别，但每帧省下一次高成本重采样。

## 深色模式下字是黑的（两处，根子不一样）

现象：切到深色主题后，页面标题、列表项、分组标题全是黑的，压在 `#202020` 上基本看不清；
页面本身还是白的，Mica 完全透不上来。

### 第一处：`Page` 不是 `Control`，隐式样式匹配不上

先别急着怀疑主题字典。`--themedump` 打出来的解析值是**对的**：

```
当前主题：Dark
TextFillColorPrimaryBrush          = SolidColorBrush #FFFFFFFF
ApplicationBackgroundBrush         = SolidColorBrush #FF202020
CardBackgroundFillColorDefaultBrush= SolidColorBrush #0DFFFFFF
```

所以问题不在资源，在控件层。`--navdump` 把某个文本的 Foreground 继承链一层层打出来，就一目了然了：

```
首个文本：'硬件信息' fg=#FF000000
    ↑ Wpf.Ui.Controls.TextBlock       fg=#FF000000
    ↑ System.Windows.Controls.StackPanel   fg=-
    ↑ Win11Toolbox.Pages.HardwarePage      fg=-
    ↑ ...
    ↑ Wpf.Ui.Controls.DynamicScrollViewer  fg=#FF000000   ← 黑色从这儿开始
    ↑ Wpf.Ui.Controls.NavigationViewContentPresenter fg=#FFFFFFFF
```

`NavigationViewContentPresenter` 是白的，到它里面的 `DynamicScrollViewer` 就变成黑的了，
再往下整棵页面子树都继承这个黑。`ScrollViewer` 没有自己设 `Foreground`，
用的是 `Control.Foreground` 的默认值 `SystemColors.WindowTextBrush`（黑）。

为什么页面没有把前景色改回来？因为 WPF-UI 的 `DefaultPageStyle` 没套上。根因是 WPF 的一条规则：

> 隐式样式（`<Style TargetType="Page">`）是按元素的 **`DefaultStyleKey`** 匹配的，
> 而 `DefaultStyleKey` 只有 `Control` 才有；`FrameworkElement` 子类（`Page`、`UserControl` 都是）
> 会退化成按 **具体类型** 匹配 —— 也就是去找 `{x:Type HardwarePage}`，永远找不到。

实测确认：`页面 Foreground=#FF000000 Background=<null> Style=<null>` —— 页面压根没有样式。
`Controls/Page/Page.xaml` 里那套 `DefaultPageStyle`（透明底 + `TextFillColorPrimaryBrush` 前景）躺在那儿没人用。
（顺便确认：`Wpf.Ui.Controls.Page` 这个类型**不存在**，那套样式就是给内置 `Page` 用的。）

修法：每个页面根节点显式写上

```xml
<Page Style="{StaticResource DefaultPageStyle}" ...>
```

改完再测：`页面 Foreground=#FFFFFFFF Background=#00FFFFFF Style=Page/6`，黑色问题当场消失。

### 第二处：显式 `Style` 顶掉了隐式样式，控件掉回 Aero2

页面标题修好之后，**列表行的标题还是黑的**（用户实机截图里整屏都是黑字）。
这时候离屏截图的判断已经不可靠了，直接上 `--rowdump`，在**真正的 MainWindow** 上量：

```
ListBox  Foreground=#FF000000  来源=DefaultStyle     ← 黑，且来源是"主题默认样式"
ListBoxItem  Foreground=#FF000000  来源=Inherited
行内文本：
    '在此处打开命令提示符' fg=#FF000000   显式=否     ← 只有这一列是黑的
    '文件夹空白处'         fg=#C5FFFFFF   显式=否     ← 写了 Foreground 的列都正常
    '正常'                 fg=#FF6CCB5F
```

关键就是 `来源=DefaultStyle`。原因是 `VirtualList` 这么写的：

```xml
<Style x:Key="VirtualList" TargetType="ListBox">   <!-- 没有 BasedOn！ -->
```

显式指定 `Style` 会**整个顶掉** WPF-UI 的隐式 ListBox 样式，控件于是掉回
`PresentationFramework.Aero2` 的主题样式。而 Aero2 主题样式里的 `Foreground` 是黑的，
这个值的优先级是 `DefaultStyle`，**高于继承** —— 所以页面那层白色前景根本传不进来。
（顺带，滚动条也变回了 Win32 那个带箭头的老滚动条。）

修法一行：

```xml
<Style x:Key="VirtualList" TargetType="ListBox" BasedOn="{StaticResource DefaultListBoxStyle}">
```

改完 `ListBox Foreground=#FFFFFFFF 来源=Inherited`，行标题正常。

> **通用教训**：只要给内置控件写了显式 `Style`（或 `ItemContainerStyle`），就等于放弃了
> WPF-UI 的那套主题样式，必须自己把 `Foreground`/`Background`/`BorderBrush` 补齐，
> 或者干脆 `BasedOn` 到 WPF-UI 的默认样式上。

### 别靠眼睛看截图，用 `--darkaudit` 扫

两处都是"局部黑字"，截图缩放后很容易看漏（第一处我一开始就漏了）。
所以加了 `--darkaudit`：在深色主题下逐页遍历所有 `TextBlock`，按亮度算一遍，
低于 0.35 的全报出来（按钮内的字跳过 —— 强调色按钮在深色下本来就是黑字配浅蓝底，属 Win11 设计）。

```
home           文本  69 个，偏暗 0 个
hardware       文本  46 个，偏暗 0 个
...
结论：深色主题下没有偏暗文字。
```

改动主题相关的东西之后跑一遍，比看图靠谱。

## 标签栏：`TabView` 会掉回 Win32 的 Aero2 模板

`ui:TabView` / `ui:TabViewItem` 在 WPF-UI 4.3.0 里 `DefaultStyleKey` 直接指向内置的
`System.Windows.Controls.TabControl`，而 `ControlsDictionary` 里只有 `TabControl.xaml`
（而且只设了 6 个 setter + 一个模板）。结果就是白底、`#ACACAC` 灰边、黑字，
一眼 Win32，跟 Win11 完全不搭。

所以整套重画，按 WinUI 3 的 `SelectorBar` 来（`App.xaml` 里的 `Win11TabControl` / `Win11TabItem`）：

- 标签只有文字，没有边框和底色；选中项字重 SemiBold + 前景色提到 `TextFillColorPrimaryBrush`；
- 选中项底部一条 3px 圆角强调色指示条（`AccentFillColorDefaultBrush`）；
- 悬停时给 `SubtleFillColorSecondaryBrush` 的圆角底；
- 整条标签栏底下一条 1px 分隔线，内容区全透明，让 Mica 透上来。

页面里就是普通的 `TabControl` + `TabItem`，`Style="{StaticResource Win11TabControl}"`。

## 首屏不能白等：轻松设置页的两段式载入

现象：点开「轻松设置」，先是几秒空白，然后几十行一下子全出来。

原因在 `LoadAsync()`：原来是

```csharp
var (items, states) = await Task.Run(() => { 全部项逐个 Probe() });   // 几秒
... 然后才第一次往 _rows 里塞行
```

`Probe()` 要读注册表，有的项还要跑 `powercfg`，几十项串起来就是好几秒。
这几秒里 `_rows` 是空的，用户看到的就是一个纯白的空列表。

改成两段：

1. **同步**建清单 + 建行（纯内存，毫秒级），立刻 `RefreshList()`。
   行的状态显示 `…`（未知），但名字、说明、风险标签全都在了。
2. 后台**分批**探测，每批 4 项，批与批之间 `await` 让出 UI 线程；
   每批回来就回填对应行，肉眼能看到它一项项亮起来。
   已经自己动过的行（`StateText != "…"`）不覆盖。

验证办法：`--shot easy out.png --pump 300` —— 只等 300ms 就渲染，
能看到行名字已经在位（改之前这里是全白的）。

## 切主题时闪一下：真凶是 UI 线程被卡了 4.3 秒

现象：亮色 ↔ 暗色切过去的**那一瞬间**，窗口边框、标题栏先变黑了，
中间的内容区还是亮底黑字，画面像被撕成两半。

### 先量，别猜

`--themeflash` 在三个时刻打印画笔值：

```
[切换前]            primary=#E4000000 appBg=#FFFAFAFA pageFg=#E4000000 winBg=#00FFFFFF backdrop=Mica
[Apply 刚返回]      primary=#FFFFFFFF appBg=#FF202020 pageFg=#FFFFFFFF winBg=#00FFFFFF backdrop=Mica
[UpdateLayout 之后] 与上一行完全相同
```

`Apply` 一返回画笔就全换完了，**WPF 这边是原子的**。

再看官方源码（`WindowBackgroundManager.UpdateBackground`），它确实会拆了重建：

```csharp
_ = WindowBackdrop.RemoveBackdrop(window);           // 拆
_ = WindowBackdrop.RemoveBackground(window);         // 拆
_ = WindowBackdrop.ApplyBackdrop(window, backdrop);  // 重建
```

看着像"窗口有一瞬间是透明的"。但三个都是同步 Win32 调用，微秒级完成，
DWM 根本来不及在中间合成一帧 —— 所以**这个方向是死胡同**，我在这上面白绕了两圈。

真正的原因在下一段计时里。

### 真凶：`NotifyThemeChanged` 卡了 4343ms

给切换过程加分段计时：

```
改主题拆解：Apply=9ms，NotifyThemeChanged=4343ms
各页耗时：Home=4278ms
```

`HomePage` 订阅的是 `RunChecks()` —— 那个方法会枚举启动项、**遍历回收站**。
换一次颜色，代价是重扫一遍回收站，UI 线程被钉死 4.3 秒。

这 4.3 秒里窗口没法重画，而标题栏已经被 DWM 切成新主题了 ——
于是"新边框 + 旧内容"的那一帧被**冻在屏幕上好几秒**，这才是截图里看到的撕裂。

改成"算一次、画一次"两段：

```csharp
private void RunChecks()    { _checks = CollectChecks(); RenderChecks(); } // 进页面 / 点重新检测
private void RenderChecks() { /* 只按 _checks 重建那几行，不碰系统 */ }    // 切主题走这里
```

修完：

```
改主题拆解：Apply=8ms，NotifyThemeChanged=2ms
各页耗时：Home=1ms
ThemeSwitch：改主题 9ms（预算 60ms 内）
```

**规则**：`UiTheme.ThemeChanged` 的订阅者只许"重画"，不许"重读"。
换颜色是纯视觉的事，重新枚举注册表 / 托盘 / 回收站是另一件事。
`UiTheme.Time("名字", 方法)` 会把耗时记进 `UiTheme.HandlerLog`，`--themeflash` 直接打出来。

### 另外两个坑：我自己给自己挖的

修这个 bug 的过程中先后加过两种"冻结帧"蒙层，两次都被用户当场打回，记在这里别再犯。

**坑一：淡出 → 整屏发灰。** 切换前抓一张亮色快照盖上去，切完淡出。
亮暗互渐必然穿过中灰：

```
0.7 × 亮色 (250,250,250) + 0.3 × 暗色 (32,40,50) = (185, 187, 190)
```

而用户截图里窗口的实测底色正是 **(185, 188, 192)** —— 差 1～3 个色阶，就是同一件事。

**坑二：硬切 → 开关卡顿。** 改成不透明蒙层、切完换新快照再一次性摘掉，灰是没了，
但这层蒙层会把 `ToggleSwitch` 正在滑的滑块盖住（详见下一节）。

结论：**换主题这条路径上什么都不要盖**。蒙层解决的是一个并不存在的问题，
代价却是压住别人的动画。现在 `ThemeSwitch.Run` 只做一件事 —— 执行 + 计时，
`--themeflash` 会断言内容区蒙层数为 0。

### 剩下的白闪：拦 `WM_ERASEBKGND`

真正需要兜的是"预擦除"：重绘前系统先发 `WM_ERASEBKGND`，用窗口类的背景刷把整块区域刷一遍，
再让 WPF 画内容 —— "先刷白、后绘图"，中间那一帧就是白闪。
WPF 自己会整块重画，不需要这一步，所以在 `MainWindow.OnSourceInitialized` 挂个钩子直接返回 1：

```csharp
private const int WM_ERASEBKGND = 0x0014;

private static IntPtr SuppressEraseBackground(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
{
    if (msg != WM_ERASEBKGND) return IntPtr.Zero;
    handled = true;
    return new IntPtr(1);
}
```

这是 WPF 里处理重绘闪烁的标准做法，没有副作用，也不影响任何动画。

### 顺带修的：诊断窗口建不起来

`--themeflash` 一度直接抛 `InvalidOperationException: ShowActivated 为 false 且 WindowState 设置为 Maximized 时无法显示窗口`。
原因是 `MainWindow` 会恢复上次的窗口状态，而用户上次是最大化关的窗口，
诊断又要求 `ShowActivated = false`（不抢焦点）—— 这两件事 WPF 不允许同时成立。
所有离屏诊断建完 `MainWindow` 后都补一句 `WindowState = WindowState.Normal`。

## 开关"顿一下再跳过去"：蒙层把滑块动画盖住了

现象：点深浅色开关，滑块从左往右滑，滑一小段就停住，然后**直接跳到最右边**。

这是上一节的"坑二"。先量清楚这个开关到底怎么动的，`--toggleanim`
（WPF-UI 4.x 的模板编译进 BAML 了，源码里看不到 `Duration`，只能运行时从
`ControlTemplate.Triggers` 里读出来）：

```
触发器 Trigger  IsChecked = True
  进入 BeginStoryboard（6 条动画）
  进入  (0) @ToggleRectangle：Double 1→0  167ms  缓动=无
  进入  (0) @ActiveToggleRectangle：Double 0→1  167ms  缓动=无
  进入  (0).(1) @ToggleEllipse：Double -9→9  167ms  缓动=无
  进入  (0).(1) @ActiveToggleEllipse：Double -9→9  167ms  缓动=无
```

滑块是两个叠在一起的 `Rectangle`（`ToggleEllipse` / `ActiveToggleEllipse`），
靠 `TranslateTransform.X` 从 **-9 滑到 9**，**167ms 线性**，连缓动函数都没有。

100ms 的不透明蒙层正好压在这 167ms 上：滑块挪一点 → 被盖住 → 蒙层摘掉时它已经在终点。

### 改法：把蒙层删了

没有蒙层，就没有东西能盖住动画。`--switchtest` 会真的去点一下开关，按 10ms 采样：

```
t(ms)  主题    蒙层数
   28  Dark    0
结论：换主题全程没有任何蒙层，滑块动画可以完整播完。
```

判据是 `t < 167ms` 这段里蒙层数必须为 0。以后谁再往内容区加覆盖层，这条断言会立刻挂掉。

顺带一提：`ToggleSwitch` 的滑块动画**没有缓动函数**，是纯线性。想要更"跟手"的观感
只能整个换模板（比如换成 `CubicEase` 或 WinUI 的 spring），但那就不是 WPF-UI 的原生观感了，
这里没动。

## 风扇控制（`Services/FanController.cs` + `Pages/FanPage.xaml`）

和 [Fan Control](https://github.com/Rem0o/FanControl.Releases)（Rem0o）同一套原理：
底层靠 LibreHardwareMonitor 跟主板 SuperIO / 显卡驱动打交道，自己只做
「读快照 / 写 PWM 百分比 / 交回自动」的薄封装。

页面分三块：**风扇曲线**（温度→转速折线，可拖起转温度/满载温度/最低转速，实时游标显示当前温度落点）、
**风扇磁贴**（转速 + 滑块 + 应用/自动）、**温度胶囊**（≥60℃ 转黄、≥80℃ 转红）。

### 三个必须知道的硬件前提

1. **读转速/温度**不需要提权；**调速**要访问主板 SuperIO，必须**管理员权限**。
   非管理员时页面顶部直接给「以管理员身份重启」按钮（走 `RightMenuActions.Elevate()`）。
2. **LHM 0.9.6 用的是 PawnIO 驱动，不是 WinRing0**（grep 过 DLL，`WinRing0`/`.sys` 全无，只有 `PawnIO`）。
   PawnIO 不随 NuGet 包分发，得单独装。没装 → 主板**一个传感器都枚举不出来**，
   `--fans` 的硬件树里主板那行会显示 `传感器:` 后面是空的。页面会给「安装 PawnIO」按钮。
3. **本机 HVCI（内存完整性）是开着的**（`SecurityServicesRunning=2`）。它会拦掉一批老的
   内核驱动，这也是「驱动痕迹: 无」的常见原因。

### 写入不生效要自己说出来

`IControl.SetSoftware()` 返回 `true` **不等于**转速真的变了。所以「应用」之后会等 1.6 秒回读一次：

```csharp
if (Math.Abs(actual.Value - target) <= 12) return;   // 差得不多，算成功
_ineffective.Add(name);                              // 记下来
SetStatus(InfoBarSeverity.Warning, "调速没有生效", ...);
```

这些风扇会被记进 `_ineffective`，状态条改成「风扇不响应软件调速」，
并且 `_verifyHold` 保证这条警告 10 秒内不被 2 秒一次的定时刷新盖掉 ——
否则用户看到的就是「点了没反应」，只会以为程序坏了。显卡的话还会带上
`AmdFan.DescribeSupport()` 的具体原因。

> 一个坑：第一次测 55% 时读回 `rpm=0`，第二次测 80% 读回 `rpm=583`，看着像「写 80% 才有效果」。
> 其实是 GPU 温度从 45℃ 升到 57℃，默认曲线自己起转了。**单次读数不能证明因果**，
> 要连做几次不同占空比、看是否单调响应。

### 驱动：程序自己带着装，不用用户去官网下

Fan Control 第一次运行时会把 PawnIO 装上，这里照做：

- `tools\PawnIO_setup.exe`（官方 2.2.0，**SHA256 = `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`**，
  3,410,960 字节）通过 `<EmbeddedResource>` **嵌进 exe**，单文件版依旧是「就一个 exe」。
- 点页面上的「安装 PawnIO」→ `FanController.StartInstaller()` 把它释放到
  `%TEMP%\Win11Toolbox\` 并启动 → 页面每 1 秒轮询 `DriverPresent` → 一出现就自动
  `Dispose()` 旧的 `Computer` 重建、重新读一遍，**不用关程序**。
- 安装器是带界面的，必须用户点两下，所以**不能 `WaitForExit`** —— 卡住会把界面冻死。
  这一点踩过：先写成同步等待，安装器窗口一弹出来整个页面就假死了。

> 装驱动这一步没法自动化测试：无桌面会话里启动 GUI 安装器会被直接杀掉。
> 所以 `--installpawnio` 只能验证「能找到安装包 + 起得来进程」，真正的点击要人来完成。

`DriverPresent` 判的是 `HKLM\SYSTEM\CurrentControlSet\Services\PawnIO` 这个服务键，
不是打开设备句柄 —— 非管理员时设备打不开，用句柄判断会误报「没装」。

### 显卡风扇：AMD 新驱动把 ADL 的调速接口关了

LHM 0.9.6 调的是 `ADL2_Overdrive5_FanSpeed_Set`（老接口）。实测本机 RX 6700 XT + Adrenalin
（ADL 7.25.10.1590）**返回成功但驱动层直接忽略**：写 0% / 60% / 80% / 100%，
读回永远是默认曲线那条（`pct=17`、`rpm≈575`）。

于是我另写了 `Services/AmdFan.cs`，直接调 RDNA 该用的 `ADL2_OverdriveN_FanControl_Set`，
结果是 **`rc=-8`（ADL_ERR_NOT_SUPPORTED）**。为排除「结构体版本不对」，
把 `iSize` 从 16 扫到 64 每个值都试了一遍，**全是 -8**；再查 `ADL2_Overdrive5_FanSpeedInfo_Get`
也返回 `rc=-1`。结论是驱动真的没开放这个接口，不是调用方式的问题：

```
读取(OverdriveN): ok=False  Get 失败 rc=-8
OD5 能力: OD5 FanSpeedInfo rc=-1
结构体大小探测: iSize=16..64 全部 rc=-8
```

AMD 把风扇控制挪到了 **ADLX**（`C:\Windows\System32\amdadlx64.dll`，5.2 MB，确实在）。
ADLX 是 C++ 接口（vtable 链），C# 要调得手写 COM 互操作，风险高，暂时没做。
现阶段显卡风扇只能用 Adrenalin 里的「自定义风扇曲线」。

诊断命令 `--adlfan [百分比]` 会把上面这些一次性打出来（含 5 秒转速采样）。

### 曲线接管与手动操作互斥

开曲线后滑块每 2 秒被读数覆盖（`Suppress` 标记防止触发 `ValueChanged` 递归）；
用户一动手动滑块/预设，`DisableCurveByManual()` 立刻把曲线关掉并落盘 ——
两边同时写 PWM 会互相打架，最后转速会抖。

曲线参数存 `%APPDATA%\Win11Toolbox\config.json`（`FanCurve` / `FanCurveStart` / `FanCurveFull` / `FanCurveMin`），
拖滑块时用 700ms 防抖再落盘，不要每个像素都写文件。

## 关掉任务管理器时卡一下：不是本程序，是任务管理器自己退了 2.3 秒

现象（用户报的）：任务管理器盖在工具箱上面，点 X 关掉它的那一瞬间，工具箱像死了一下。

### 先量，别猜

`--uistall` 挂两条探针：

- **UI 线程探针**：8ms 的 `DispatcherPriority.Send` 计时器。它每次触发时算「距上次触发过了多久」，
  正常情况下接近 8ms；隔了 300ms 就说明这 300ms 里 UI 线程根本轮不到它。
- **DWM 探针**：后台线程连续 `DwmFlush()`。WPF 绘制分两段——UI 线程算视觉树、渲染线程光栅化。
  渲染线程或整个桌面合成卡住时，UI 线程照样很闲，但窗口就是不动。所以必须单独量合成。

再挂钩子记下 `WM_ACTIVATE` / `WM_PAINT` 的到达时刻，把两条探针和窗口消息对到同一条时间线上。

### 结果

| 场景 | UI 线程卡顿 | DWM 合成卡顿 | 关闭 → 重新激活 |
|---|---|---|---|
| 系统工具页，安静待着 | 0 次 | 0 次 | — |
| 任务管理器，优雅关闭（`WM_CLOSE`） | **0 次** | **0 次** | **2308ms** |
| 任务管理器，强杀（`Kill`） | 0 次 | 0 次 | **15ms** |
| 完全不开本程序，PowerShell 单独开关任务管理器 | — | — | 任务管理器退出用 **2318ms** |

三件事同时成立：UI 线程一秒都没被占、桌面合成一次都没卡、任务管理器自己退出就要 2.3 秒。
整页重绘成本也量了：1275×820 全量软件渲染 **17ms**，不是绘制的问题。

强杀 vs 优雅关闭差了 150 倍，说明时间全花在任务管理器的**关闭流程**上
（保存设置、注销通知、回收进程枚举线程）。它在这 2.3 秒里一直握着前台窗口，
而 Windows 要等前台窗口销毁才把焦点交还给下面那个窗口 —— 于是用户点什么都没反应。
换成记事本盖上来再关，本程序 15ms 内就拿回焦点。

**结论：不是本程序的 bug，任何程序被任务管理器盖住都会这样。** 但为了能随时复查，
`--uistall` 留下来了：以后再有"卡"的报告，先跑它，一眼就能分清是 UI 线程、渲染、还是系统。

### 顺带查出来的真问题：每秒 15–22ms 白烧在 UI 线程上

`--monitorcost` 量主页那套每秒采样（本机 303 个进程、23 张在用网卡）：

| 步骤 | 耗时 |
|---|---|
| `Process.GetProcesses().Length` | 3.6–5.4ms |
| `NetworkInterface.GetAllNetworkInterfaces()` + 逐张统计 | **9.8–16.7ms** |
| `DriveInfo.AvailableFreeSpace` | 0.0–0.1ms |
| `EnumProcesses`（替代方案） | **0.1–0.6ms** |

原来这些都跑在 `DispatcherTimer` 里，也就是每秒有 15–22ms 在 UI 线程上做跟界面无关的事。
平时感觉不到，但正好撞上重绘/切页就会顿一下。改法：

1. 采样搬到 `System.Threading.Timer`（线程池），UI 线程只负责最后把数字塞进控件，
   而且用 `DispatcherPriority.Background` —— 有重绘、有输入就先让路。
2. 进程数改用 `EnumProcesses`（`psapi.dll`）。`Process.GetProcesses()` 慢是因为它要给
   每个进程建一个 `Process` 对象并开句柄，而我们只要一个数字。
3. 加 `_busy` 互斥：上一轮没算完就跳过这一轮，慢机器上不会越堆越多。

> 坑：`--uistall 18 a.txt --page tools` 里的 `tools` 被当成日志文件名，而 `tools` 恰好是
> 项目里的目录 → `File.WriteAllLines` 抛异常 → 被 `catch { }` 吞掉 → **日志静默消失，看着像没跑**。
> 现在解析参数会跳过 `--page` / `--theme` 后面的值，写日志失败也会退回程序目录并报 stderr。

## 2FA 验证码：不落盘是硬约束（`Services/Totp.cs` + `Controls/CountdownRing.cs`）

要求很明确：**能用，但不许保存密钥**。所以这里没有「记住上次输入」、没有配置项、
没有历史记录，连缓存都没有：

| 位置 | 有没有密钥 |
| --- | --- |
| `config.json` / `hardware.json` | 没有（这两个文件里一个字段都没加） |
| `error.log` | 没有（异常信息里不带密钥） |
| 剪贴板 | 只有验证码，且 45 秒后自动抹掉 |
| 内存 | `byte[]`，删条目 / 关程序时 `Array.Clear` 清零 |

页面卸载不会销毁账户（切到别的页再回来，账户还在），但**退出程序一定清空** ——
`TotpAccount` 只被页面的 `ObservableCollection` 持有，页面一回收就跟着走。

### Base32 的那个坑：`'6'` 的 ASCII 比 `'Z'` 小

自己解 Base32 时写了 `int value = c <= 'Z' ? c - 'A' : c - '2' + 26;`，
看着没问题，实际把数字也当成了字母：`'6'` 是 54，`'Z'` 是 90，`54 <= 90` 成立 →
`'6' - 'A' = -11`。密钥里只要有数字，解出来就全歪。

**这个 bug 不会报错**：验证码照样跳 6 位数字，倒计时照样走，只有登录时被网站拒绝，
用户第一反应会是自己抄错了。`--totp` 用 RFC 6238 的 18 组官方向量把它钉住了
（修之前 Base32 那两条直接红）。

### 倒计时圆圈：为什么不直接用 `Ellipse`

`StrokeDashArray` 的长度单位是**线宽的倍数**，圆环半径一变就得重算，DPI 缩放后对不齐，
圆头端帽还会被裁掉。`CountdownRing` 在 `OnRender` 里按当前尺寸画 `ArcSegment`，
尺寸怎么变都准；满圈时（≥99.95%）退化成 `DrawEllipse`，因为起止点重合的弧几何是空的。

颜色分四段（以 30 秒周期算）：

| 剩余 | 颜色 | 时间 |
| --- | --- | --- |
| ≥ 75% | 绿 | 前 7.5 秒 |
| 75%→50% | 绿渐黄 | 7.5–15 秒 |
| 50%→20% | 黄 | 15–24 秒 |
| < 20% | 黄渐红 | 最后 6 秒 |

用的是主题里的 `SystemFillColorSuccess/Caution/Critical`，深浅色自动换，
不写死 `#0F7B0F` 这种只在浅色下成立的值。`--ringsheet a.png` 一次把 8 档都画出来核对。

## 已知差异

- 导航选中项左侧有一道蓝色指示条（WPF-UI 的默认样式），系统「设置」应用里已经没有这道条了。
- 子页面切换用的是「文字 + 底部强调色指示条」的标签栏（按 WinUI 3 `SelectorBar` 重画），
  而系统「设置」应用用的是面包屑 + 列表，形态不同但都是 Win11 的语汇。
- 页面留白与行距按 Win11 的 24px 栅格来，个别卡片的内边距仍与官方有 1–2px 出入。
