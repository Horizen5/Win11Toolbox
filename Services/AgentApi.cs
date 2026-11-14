using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Win11Toolbox.SettingsKit;
using Win11Toolbox.Tray;

namespace Win11Toolbox.Services;

/// <summary>
/// 本机 HTTP API：给 Agent / 脚本读取工具箱能看到的一切系统数据。
///
/// <para>设计约束（有意为之，别改）：</para>
/// <list type="bullet">
/// <item>只绑回环地址（127.0.0.1 / localhost），不监听 0.0.0.0，外部机器连不上。</item>
/// <item>所有 /api/* 都要 Token；Token 首次自动生成在 %APPDATA%\Win11Toolbox\api-token.txt。</item>
/// <item>默认只读。写操作（调速、启动工具、改设置）必须用 <c>--allow-actions</c> 启动才开放，
///       因为 ToolCatalog.Launch 会真的拉起进程、SetPercent 会真的写主板寄存器。</item>
/// <item>不引入任何新依赖：HttpListener + System.Text.Json 都在框架 / 已有包里，
///       所以 4.8.1 版加这个功能体积不变。</item>
/// </list>
/// </summary>
public static class AgentApi
{
    public const int DefaultPort = 5180;
    public const string ApiVersion = "1";

    // ------------------------------------------------------------------ 状态

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 内存条 / 硬盘 / 硬盘分区这些模型用的是公有字段而不是属性
        IncludeFields = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // LHM 读不到的传感器会给 NaN；默认序列化器遇到 NaN 直接抛异常
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>写文件一律不带 BOM：带 BOM 的 JSON / Token 会被别的语言的解析器当成非法字符。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private static readonly object LogGate = new();
    private static readonly object HardwareGate = new();
    private static readonly object AppsGate = new();
    private static readonly object VoltageGate = new();

    private static HttpListener? _listener;
    private static string _token = "";
    private static bool _allowActions;
    private static int _port = DefaultPort;
    private static DateTime _startedAt = DateTime.Now;
    private static string _logPath = "";

    private static HardwareReport? _hardwareCache;
    private static DateTime _hardwareCacheAt = DateTime.MinValue;

    private static List<InstalledApp>? _appsCache;
    private static DateTime _appsCacheAt = DateTime.MinValue;

    private static readonly VoltageMonitor Voltage = new();

    // ------------------------------------------------------------------ 路径

    public static string StateFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Win11Toolbox");

    public static string TokenPath => Path.Combine(StateFolder, "api-token.txt");

    /// <summary>端口 / PID / Token 路径，Agent 可读它自己发现服务，不用猜。</summary>
    public static string RuntimePath => Path.Combine(StateFolder, "api-runtime.json");

    // ------------------------------------------------------------------ 入口

    /// <summary>启动 API 服务并阻塞。返回进程退出码（0 正常，3 端口占用/绑定失败）。</summary>
    public static int Run(int port, bool allowActions, string? tokenOverride = null, string? logPath = null)
    {
        _port = port;
        _allowActions = allowActions;
        _startedAt = DateTime.Now;
        _logPath = logPath ?? Path.Combine(StateFolder, "api.log");

        try { Directory.CreateDirectory(StateFolder); } catch { }

        _token = !string.IsNullOrWhiteSpace(tokenOverride)
            ? tokenOverride.Trim()
            : LoadOrCreateToken();

        var prefixes = new[]
        {
            "http://127.0.0.1:" + port + "/",
            "http://localhost:" + port + "/",
        };

        Exception? last = null;
        foreach (var prefix in prefixes)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _listener = listener;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                try { listener.Close(); } catch { }
            }
        }

        if (_listener is null)
        {
            string hint = last is null ? "未知原因"
                : last.GetType().Name + ": " + last.Message;
            WriteLog("绑定 " + port + " 失败 —— " + hint);
            WriteLog("提示：端口可能已被占用；非管理员且 localhost 前缀也失败时，可执行 " +
                     "netsh http add urlacl url=http://127.0.0.1:" + port + "/ user=%USERNAME%");
            return 3;
        }

        WriteRuntime();
        WriteLog("API 已启动 http://127.0.0.1:" + port + "  actions=" + (_allowActions ? "on" : "off"));
        WriteLog("Token 文件: " + TokenPath);

        try
        {
            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch { try { ctx.Response.Abort(); } catch { } }
                });
            }
        }
        finally
        {
            try { _listener?.Close(); } catch { }
            TryDeleteOwnRuntime();
            WriteLog("API 已停止");
        }

        return 0;
    }

    // ------------------------------------------------------------------ Token

    private static string LoadOrCreateToken()
    {
        try
        {
            if (File.Exists(TokenPath))
            {
                var existing = File.ReadAllText(TokenPath).Trim();
                if (existing.Length >= 16)
                    return existing;
            }
        }
        catch { }

        var buffer = new byte[16];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            rng.GetBytes(buffer);
        var token = HexCompat.ToHexString(buffer).ToLowerInvariant();

        try { File.WriteAllText(TokenPath, token + Environment.NewLine, Utf8NoBom); }
        catch { }
        return token;
    }

    /// <summary>多个实例并存时，别把别人写的 api-runtime.json 删掉。</summary>
    private static void TryDeleteOwnRuntime()
    {
        try
        {
            if (!File.Exists(RuntimePath)) return;
            var text = File.ReadAllText(RuntimePath);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("pid", out var pid) &&
                pid.TryGetInt32(out int value) &&
                value == Process.GetCurrentProcess().Id)
                File.Delete(RuntimePath);
        }
        catch { }
    }

    private static void WriteRuntime()
    {
        try
        {
            var payload = new
            {
                port = _port,
                url = "http://127.0.0.1:" + _port + "/",
                pid = Process.GetCurrentProcess().Id,
                startedAt = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                actionsEnabled = _allowActions,
                tokenFile = TokenPath,
            };
            File.WriteAllText(RuntimePath, JsonSerializer.Serialize(payload, JsonOptions), Utf8NoBom);
        }
        catch { }
    }

    // ------------------------------------------------------------------ 日志

    private static void WriteLog(string line)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(StateFolder);
                if (File.Exists(_logPath))
                {
                    try
                    {
                        if (new FileInfo(_logPath).Length > 1024 * 1024)
                            File.Copy(_logPath, _logPath + ".old", true);
                    }
                    catch { }
                }
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + "\r\n", Utf8NoBom);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ 请求分发

    private static void Handle(HttpListenerContext ctx)
    {
        var watch = Stopwatch.StartNew();
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            path = path.TrimEnd('/');

        try
        {
            // 索引和 OpenAPI 不需要 Token：里面只有端点清单，没有任何机器数据
            if (path == "/" || path == "/api" || path == "/index.json")
            {
                Json(ctx, 200, Index());
                return;
            }

            if (path == "/openapi.json")
            {
                Json(ctx, 200, OpenApi());
                return;
            }

            if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                Json(ctx, 404, Error("未知路径。GET / 可以看到全部端点。"));
                return;
            }

            if (!TokenOk(ctx))
            {
                Json(ctx, 401, Error(
                    "缺少或错误的 Token。请带请求头 Authorization: Bearer <token>，" +
                    "Token 在 " + TokenPath));
                return;
            }

            Dispatch(ctx, path);
        }
        catch (Exception ex)
        {
            try { Json(ctx, 500, Error(ex.GetType().Name + ": " + ex.Message)); } catch { }
            WriteLog("ERR " + path + " → " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            WriteLog(ctx.Request.HttpMethod + " " + path + " → " + ctx.Response.StatusCode +
                     "  " + watch.ElapsedMilliseconds + "ms");
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static void Dispatch(HttpListenerContext ctx, string path)
    {
        var method = ctx.Request.HttpMethod;

        switch (path.ToLowerInvariant())
        {
            case "/api/v1/health":
                Json(ctx, 200, Health());
                return;

            case "/api/v1/system":
                Json(ctx, 200, SystemStatus());
                return;

            case "/api/v1/hardware":
                Json(ctx, 200, Hardware(refresh: QueryBool(ctx, "refresh")));
                return;

            case "/api/v1/sensors":
                Json(ctx, 200, SensorsOnly());
                return;

            case "/api/v1/voltages":
                Json(ctx, 200, VoltagesOnly());
                return;

            case "/api/v1/snapshot":
                Json(ctx, 200, Snapshot());
                return;

            case "/api/v1/tools":
                Json(ctx, 200, Tools());
                return;

            case "/api/v1/settings":
                Json(ctx, 200, Settings());
                return;

            case "/api/v1/tray":
                Json(ctx, 200, TrayItems());
                return;

            case "/api/v1/apps":
                Json(ctx, 200, Apps(QueryBool(ctx, "refresh")));
                return;

            case "/api/v1/tools/launch":
                if (!RequirePost(ctx)) return;
                if (!RequireActions(ctx)) return;
                Json(ctx, 200, LaunchTool(ctx));
                return;

            case "/api/v1/fan/set":
                if (!RequirePost(ctx)) return;
                if (!RequireActions(ctx)) return;
                Json(ctx, 200, SetFan(ctx, auto: false));
                return;

            case "/api/v1/fan/auto":
                if (!RequirePost(ctx)) return;
                if (!RequireActions(ctx)) return;
                Json(ctx, 200, SetFan(ctx, auto: true));
                return;

            case "/api/v1/settings/set":
                if (!RequirePost(ctx)) return;
                if (!RequireActions(ctx)) return;
                Json(ctx, 200, SetSetting(ctx));
                return;

            default:
                Json(ctx, 404, Error("没有这个端点：" + path));
                return;
        }
    }

    // ------------------------------------------------------------------ 端点实现

    private static object Health()
    {
        return new
        {
            ok = true,
            name = "Win11 工具箱",
            version = Version(),
            apiVersion = ApiVersion,
            port = _port,
            url = "http://127.0.0.1:" + _port + "/",
            startedAt = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            uptimeSeconds = (long)(DateTime.Now - _startedAt).TotalSeconds,
            elevated = FanController.IsElevated,
            pawnIoDriver = FanController.DriverPresent,
            actionsEnabled = _allowActions,
        };
    }

    private static object SystemStatus()
    {
        var mem = NativeMethods.Memory();
        var os = Environment.OSVersion;
        double cpu;
        try { cpu = Math.Round(NativeMethods.CpuUsage(), 1); } catch { cpu = double.NaN; }

        return new
        {
            ok = true,
            collectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            cpuPercent = Num(cpu, 1),
            memory = new
            {
                usedBytes = mem.Used,
                totalBytes = mem.Total,
                percent = Num(mem.Percent, 1),
                usedText = ByteFormatter.Format(mem.Used),
                totalText = ByteFormatter.Format(mem.Total),
            },
            processCount = SafeInt(NativeMethods.ProcessCount),
            machine = new
            {
                computerName = Environment.MachineName,
                user = Environment.UserName,
                os = os.VersionString,
                version = os.Version.ToString(),
                build = os.Version.Build.ToString(),
                is64Bit = Environment.Is64BitOperatingSystem,
                processorCount = Environment.ProcessorCount,
                systemDrive = Path.GetPathRoot(Environment.SystemDirectory),
            },
            uptimeSeconds = SafeUptimeSeconds(),
        };
    }

    private static object Hardware(bool refresh)
    {
        var report = LoadHardware(refresh);
        return new
        {
            ok = true,
            collectedAt = report.CollectedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            fromDiskCache = DateTime.Now - report.CollectedAt > TimeSpan.FromSeconds(5),
            report,
        };
    }

    private static HardwareReport LoadHardware(bool refresh)
    {
        lock (HardwareGate)
        {
            if (!refresh && _hardwareCache is not null &&
                DateTime.Now - _hardwareCacheAt < TimeSpan.FromMinutes(10))
                return _hardwareCache;

            if (!refresh)
            {
                var cached = HardwareStore.Load();
                if (cached is not null)
                {
                    _hardwareCache = cached;
                    _hardwareCacheAt = DateTime.Now;
                    return cached;
                }
            }

            var report = SystemInfo.Build(true);
            HardwareStore.Save(report);
            _hardwareCache = report;
            _hardwareCacheAt = DateTime.Now;
            return report;
        }
    }

    private static object SensorsOnly()
    {
        var (fans, temps) = Sample();
        return SensorsPayload(fans, temps);
    }

    private static object SensorsPayload(List<FanChannel> fans, List<TempReading> temps)
    {
        var controller = FanController.Shared;
        return new
        {
            ok = true,
            collectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            superIoDetected = controller.SuperIoDetected,
            cpuTempAvailable = controller.CpuTempAvailable,
            driverPresent = FanController.DriverPresent,
            elevated = FanController.IsElevated,
            fanCount = fans.Count,
            tempCount = temps.Count,
            fans = fans.Select((f, i) => new
            {
                index = i,
                name = f.Name,
                hardware = f.Hardware,
                rpm = Num(f.Rpm ?? float.NaN, 0),
                percent = Num(f.Percent ?? float.NaN, 1),
                controllable = f.Controllable,
                minPercent = Num(f.MinPercent, 0),
                maxPercent = Num(f.MaxPercent, 0),
            }).ToArray(),
            temps = temps.Select(t => new
            {
                hardware = t.Hardware,
                name = t.Name,
                celsius = Num(t.Value, 1),
            }).ToArray(),
        };
    }

    private static object VoltagesOnly()
    {
        var rails = UpdateVoltages();
        return new
        {
            ok = true,
            collectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            railCount = rails.Rails.Count,
            otherCount = rails.Others.Count,
            rails = rails.Rails.Select(RailDto).ToArray(),
            others = rails.Others.Select(RailDto).ToArray(),
        };
    }

    private static object RailDto(VoltageReading r)
    {
        return new
        {
            label = r.Label,
            rail = r.Rail.ToString(),
            hardware = r.Hardware,
            name = r.Name,
            identifier = r.Identifier,
            nominal = Num(r.Nominal, 1),
            value = Num(r.Value, 2),
            raw = Num(r.Raw, 3),
            gain = Num(r.Gain, 2),
            min = Num(r.Min, 2),
            max = Num(r.Max, 2),
            hasBaseline = r.HasBaseline,
            baseline = r.HasBaseline ? Num(r.Baseline, 2) : null,
            dropPercent = r.HasBaseline ? Num(r.DropPercent, 2) : null,
            inTolerance = r.InTolerance,
            toleranceLow = Num(r.ToleranceLow, 2),
            toleranceHigh = Num(r.ToleranceHigh, 2),
        };
    }

    private static object Snapshot()
    {
        var (fans, temps) = Sample();
        var rails = UpdateVoltages();
        var mem = NativeMethods.Memory();

        return new
        {
            ok = true,
            collectedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            system = new
            {
                cpuPercent = Num(SafeDouble(NativeMethods.CpuUsage), 1),
                memoryPercent = Num(mem.Percent, 1),
                memoryUsedBytes = mem.Used,
                memoryTotalBytes = mem.Total,
                processCount = SafeInt(NativeMethods.ProcessCount),
                uptimeSeconds = SafeUptimeSeconds(),
            },
            sensors = SensorsPayload(fans, temps),
            voltages = new
            {
                rails = rails.Rails.Select(RailDto).ToArray(),
                others = rails.Others.Select(RailDto).ToArray(),
            },
        };
    }

    private static object Tools()
    {
        return new
        {
            ok = true,
            count = ToolCatalog.TotalCount,
            categories = ToolCatalog.Categories.Select(c => new
            {
                title = c.Title,
                items = c.Items.Select(i => new
                {
                    name = i.Name,
                    description = i.Description,
                    target = i.Target,
                }).ToArray(),
            }).ToArray(),
        };
    }

    private static object Settings()
    {
        var list = EasySettingsData.Build();
        var groups = list
            .GroupBy(i => i.Category)
            .Select(g => new
            {
                category = g.Key,
                items = g.Select(i => new
                {
                    name = i.Name,
                    description = i.Desc,
                    enabled = Probe(i),
                    needAdmin = i.NeedAdmin,
                    needExplorerRestart = i.NeedExplorerRestart,
                    dangerous = i.Danger,
                    risk = string.IsNullOrEmpty(i.Risk) ? null : i.Risk,
                    registryPath = string.IsNullOrEmpty(i.RegPath) ? null : i.RegPath,
                    guideUrl = string.IsNullOrEmpty(i.GuideUrl) ? null : i.GuideUrl,
                }).ToArray(),
            })
            .ToArray();

        return new
        {
            ok = true,
            count = list.Count,
            categories = groups,
        };
    }

    private static object TrayItems()
    {
        var log = new StringBuilder();
        var items = TrayCore.Load(log);

        return new
        {
            ok = true,
            count = items.Count,
            items = items.Select(t => new
            {
                app = t.AppName,
                settingsName = string.IsNullOrEmpty(t.SettingsName) ? null : t.SettingsName,
                trayKey = string.IsNullOrEmpty(t.SettingsKey) ? null : t.SettingsKey,
                where = t.Where,
                live = t.Live,
                exePath = string.IsNullOrEmpty(t.ExePath) ? null : t.ExePath,
                state = t.SettingsState,
                tooltip = string.IsNullOrEmpty(t.Tooltip) ? null : t.Tooltip,
            }).ToArray(),
        };
    }

    private static object Apps(bool refresh)
    {
        lock (AppsGate)
        {
            if (!refresh && _appsCache is not null &&
                DateTime.Now - _appsCacheAt < TimeSpan.FromMinutes(5))
                return AppsPayload(_appsCache);

            var list = AppManager.LoadAppsAsync(false, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            _appsCache = list;
            _appsCacheAt = DateTime.Now;
            return AppsPayload(list);
        }
    }

    private static object AppsPayload(List<InstalledApp> list)
    {
        return new
        {
            ok = true,
            count = list.Count,
            items = list.Select(a => new
            {
                name = a.Name,
                version = a.Version,
                source = a.Source,
                packageFullName = a.PackageFullName,
            }).ToArray(),
        };
    }

    // ------------------------------------------------------------------ 动作

    private static object LaunchTool(HttpListenerContext ctx)
    {
        var body = ReadBody(ctx);
        string? name = JsonString(body, "name");
        string? target = JsonString(body, "target");

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(target))
            return Error("请求体需要 name 或 target，例如 {\"name\":\"记事本\"}。没有该工具就先 GET /api/v1/tools。");

        var item = ToolCatalog.Categories
            .SelectMany(c => c.Items)
            .FirstOrDefault(i =>
                (!string.IsNullOrWhiteSpace(name) && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target) && string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)));

        if (item is null)
            return Error("工具清单里没有「" + (name ?? target) + "」。");

        string? message = ToolCatalog.Launch(item);
        return new
        {
            ok = true,
            launched = item.Name,
            target = item.Target,
            message = message ?? "已启动",
        };
    }

    private static object SetFan(HttpListenerContext ctx, bool auto)
    {
        var controller = FanController.Shared;
        if (!controller.Open())
            return Error("硬件层没打开：" + (controller.LastError ?? "未知原因"));
        if (!FanController.DriverPresent)
            return Error("没检测到 PawnIO 驱动，调速不可用（读取温度/转速仍可用）。");
        if (!FanController.IsElevated)
            return Error("调速需要管理员权限，请以管理员身份启动。");

        var (fans, _) = controller.Snapshot();
        if (fans.Count == 0)
            return Error("没有读到可用的风扇通道。");

        var body = ReadBody(ctx);
        int? index = JsonInt(body, "index");
        string? name = JsonString(body, "name");
        double? percent = JsonDouble(body, "percent");

        FanChannel? channel = null;
        if (index.HasValue)
            channel = index.Value >= 0 && index.Value < fans.Count ? fans[index.Value] : null;
        else if (!string.IsNullOrWhiteSpace(name))
            channel = fans.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
            return Error("没找到指定通道。可用通道：" +
                         string.Join("、", fans.Select((f, i) => i + "=" + f.Name)));

        if (!auto)
        {
            if (!percent.HasValue)
                return Error("缺少 percent（0-100）。");
            if (!channel.Controllable)
                return Error("通道「" + channel.Name + "」不支持软件调速。");

            bool ok = controller.SetPercent(channel, (float)percent.Value);
            return new
            {
                ok,
                channel = channel.Name,
                percent = channel.Percent,
                error = ok ? null : (controller.LastError ?? "写入失败"),
            };
        }

        bool autoOk = controller.SetAuto(channel);
        return new
        {
            ok = autoOk,
            channel = channel.Name,
            auto = true,
            error = autoOk ? null : (controller.LastError ?? "恢复默认失败"),
        };
    }

    private static object SetSetting(HttpListenerContext ctx)
    {
        var body = ReadBody(ctx);
        string? name = JsonString(body, "name");
        bool? enabled = JsonBool(body, "enabled");

        if (string.IsNullOrWhiteSpace(name) || !enabled.HasValue)
            return Error("请求体需要 name 和 enabled，例如 {\"name\":\"显示已知文件扩展名\",\"enabled\":true}。");

        var item = EasySettingsData.Build()
            .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        if (item is null)
            return Error("设置清单里没有「" + name + "」。名字必须与 GET /api/v1/settings 返回的完全一致。");

        if (!string.IsNullOrEmpty(item.GuideUrl))
            return Error("这一项系统不允许程序直接改，需要用户手动操作：" + item.GuideUrl);

        if (item.NeedAdmin && !FanController.IsElevated)
            return Error("这一项需要管理员权限，请以管理员身份启动。");

        try
        {
            item.Set(enabled.Value);
        }
        catch (Exception ex)
        {
            return Error("写入失败：" + ex.Message);
        }

        return new
        {
            ok = true,
            name = item.Name,
            requested = enabled.Value,
            actual = Probe(item),
            needExplorerRestart = item.NeedExplorerRestart,
        };
    }

    // ------------------------------------------------------------------ 采样辅助

    private static (List<FanChannel> Fans, List<TempReading> Temps) Sample()
    {
        try
        {
            return FanController.Shared.Snapshot();
        }
        catch
        {
            return (new List<FanChannel>(), new List<TempReading>());
        }
    }

    private static VoltageMonitor UpdateVoltages()
    {
        lock (VoltageGate)
        {
            var controller = FanController.Shared;
            controller.Snapshot();
            Voltage.Update(controller.Voltages);
            return Voltage;
        }
    }

    // ------------------------------------------------------------------ 索引 / OpenAPI

    private static object Index()
    {
        return new
        {
            ok = true,
            name = "Win11 工具箱 · 本机 API",
            apiVersion = ApiVersion,
            note = "所有 /api/v1/* 都需要请求头 Authorization: Bearer <token>；Token 见 api-token.txt。",
            tokenFile = TokenPath,
            endpoints = new object[]
            {
                new { method = "GET", path = "/api/v1/health", desc = "服务状态、版本、权限、驱动" },
                new { method = "GET", path = "/api/v1/system", desc = "CPU / 内存占用、进程数、开机时长" },
                new { method = "GET", path = "/api/v1/hardware", desc = "CPU / 主板 / 内存条 / 显卡 / 硬盘 / 分区 / 网卡" },
                new { method = "GET", path = "/api/v1/sensors", desc = "风扇转速、温度" },
                new { method = "GET", path = "/api/v1/voltages", desc = "三路主轨与其他电压（含压降）" },
                new { method = "GET", path = "/api/v1/snapshot", desc = "system + sensors + voltages 一次取全" },
                new { method = "GET", path = "/api/v1/tools", desc = "47 项系统工具清单" },
                new { method = "GET", path = "/api/v1/settings", desc = "65 项轻松设置及当前状态" },
                new { method = "GET", path = "/api/v1/tray", desc = "托盘图标清单与位置" },
                new { method = "GET", path = "/api/v1/apps", desc = "已安装应用（走 winget/Appx，较慢）" },
                new { method = "POST", path = "/api/v1/tools/launch", desc = "启动一个工具（需 --allow-actions）" },
                new { method = "POST", path = "/api/v1/fan/set", desc = "设置风扇百分比（需 --allow-actions）" },
                new { method = "POST", path = "/api/v1/fan/auto", desc = "风扇交回 BIOS 控制（需 --allow-actions）" },
                new { method = "POST", path = "/api/v1/settings/set", desc = "改一项设置（需 --allow-actions）" },
                new { method = "GET", path = "/openapi.json", desc = "机器可读的接口描述" },
            },
        };
    }

    private static object OpenApi()
    {
        object Id(string type) => new { type };

        object Response(string desc, string type = "object")
            => new
            {
                description = desc,
                content = new Dictionary<string, object>
                {
                    ["application/json"] = new { schema = new { type } },
                },
            };

        var paths = new Dictionary<string, object>
        {
            ["/api/v1/health"] = new
            {
                get = new
                {
                    summary = "服务状态",
                    responses = new Dictionary<string, object> { ["200"] = Response("健康信息") },
                },
            },
            ["/api/v1/system"] = new
            {
                get = new
                {
                    summary = "CPU / 内存 / 进程数 / 开机时长",
                    responses = new Dictionary<string, object> { ["200"] = Response("系统状态") },
                },
            },
            ["/api/v1/hardware"] = new
            {
                get = new
                {
                    summary = "硬件清单（WMI + 缓存）",
                    parameters = new object[]
                    {
                        new { name = "refresh", @in = "query", required = false, schema = Id("boolean"), description = "true=强制重新采集（约 2 秒）" },
                    },
                    responses = new Dictionary<string, object> { ["200"] = Response("硬件报告") },
                },
            },
            ["/api/v1/sensors"] = new
            {
                get = new
                {
                    summary = "风扇转速与温度",
                    responses = new Dictionary<string, object> { ["200"] = Response("传感器读数") },
                },
            },
            ["/api/v1/voltages"] = new
            {
                get = new
                {
                    summary = "电压轨道（+12V / +5V / +3.3V 及其他）",
                    responses = new Dictionary<string, object> { ["200"] = Response("电压读数") },
                },
            },
            ["/api/v1/snapshot"] = new
            {
                get = new
                {
                    summary = "system + sensors + voltages 聚合快照",
                    responses = new Dictionary<string, object> { ["200"] = Response("聚合快照") },
                },
            },
            ["/api/v1/tools"] = new
            {
                get = new
                {
                    summary = "系统工具清单",
                    responses = new Dictionary<string, object> { ["200"] = Response("工具清单") },
                },
            },
            ["/api/v1/settings"] = new
            {
                get = new
                {
                    summary = "轻松设置清单及状态",
                    responses = new Dictionary<string, object> { ["200"] = Response("设置清单") },
                },
            },
            ["/api/v1/tray"] = new
            {
                get = new
                {
                    summary = "托盘图标清单",
                    responses = new Dictionary<string, object> { ["200"] = Response("托盘清单") },
                },
            },
            ["/api/v1/apps"] = new
            {
                get = new
                {
                    summary = "已安装应用（较慢，默认缓存 5 分钟）",
                    parameters = new object[]
                    {
                        new { name = "refresh", @in = "query", required = false, schema = Id("boolean") },
                    },
                    responses = new Dictionary<string, object> { ["200"] = Response("应用清单") },
                },
            },
            ["/api/v1/tools/launch"] = new
            {
                post = new
                {
                    summary = "启动一个工具（需 --allow-actions）",
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["name"] = Id("string"),
                                        ["target"] = Id("string"),
                                    },
                                },
                            },
                        },
                    },
                    responses = new Dictionary<string, object>
                    {
                        ["200"] = Response("启动结果"),
                        ["403"] = Response("未开启动作"),
                    },
                },
            },
            ["/api/v1/fan/set"] = new
            {
                post = new
                {
                    summary = "设置风扇百分比（需 --allow-actions）",
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["index"] = Id("integer"),
                                        ["name"] = Id("string"),
                                        ["percent"] = Id("number"),
                                    },
                                    required = new[] { "percent" },
                                },
                            },
                        },
                    },
                    responses = new Dictionary<string, object> { ["200"] = Response("调速结果") },
                },
            },
            ["/api/v1/fan/auto"] = new
            {
                post = new
                {
                    summary = "风扇交回 BIOS（需 --allow-actions）",
                    responses = new Dictionary<string, object> { ["200"] = Response("恢复结果") },
                },
            },
            ["/api/v1/settings/set"] = new
            {
                post = new
                {
                    summary = "修改一项设置（需 --allow-actions）",
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["name"] = Id("string"),
                                        ["enabled"] = Id("boolean"),
                                    },
                                    required = new[] { "name", "enabled" },
                                },
                            },
                        },
                    },
                    responses = new Dictionary<string, object> { ["200"] = Response("设置结果") },
                },
            },
        };

        return new Dictionary<string, object>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new
            {
                title = "Win11 工具箱 · 本机 API",
                version = ApiVersion,
                description = "只监听回环地址；除 / 与 /openapi.json 外都需要 Bearer Token。",
            },
            ["servers"] = new object[] { new { url = "http://127.0.0.1:" + _port } },
            ["components"] = new
            {
                securitySchemes = new Dictionary<string, object>
                {
                    ["bearerAuth"] = new { type = "http", scheme = "bearer" },
                },
            },
            ["security"] = new object[] { new Dictionary<string, object> { ["bearerAuth"] = Array.Empty<string>() } },
            ["paths"] = paths,
        };
    }

    // ------------------------------------------------------------------ HTTP 工具

    private static bool TokenOk(HttpListenerContext ctx)
    {
        var header = ctx.Request.Headers["Authorization"];
        string? token = null;

        if (!string.IsNullOrEmpty(header) &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header.Substring(7).Trim();

        if (string.IsNullOrEmpty(token))
            token = ctx.Request.Headers["X-Api-Token"];

        if (string.IsNullOrEmpty(token))
            token = ctx.Request.QueryString["token"];

        return !string.IsNullOrEmpty(token) && string.Equals(token, _token, StringComparison.Ordinal);
    }

    private static bool RequirePost(HttpListenerContext ctx)
    {
        if (string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return true;

        Json(ctx, 405, Error("这个端点只接受 POST。"));
        return false;
    }

    private static bool RequireActions(HttpListenerContext ctx)
    {
        if (_allowActions)
            return true;

        Json(ctx, 403, Error(
            "服务是只读模式。要执行操作，请用 --allow-actions 启动，例如：Win11工具箱.exe --api --allow-actions"));
        return false;
    }

    private static void Json(HttpListenerContext ctx, int status, object payload)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // 客户端提前断开很常见，不用管
        }
    }

    private static object Error(string message) => new { ok = false, error = message };

    private static bool QueryBool(HttpListenerContext ctx, string key)
    {
        var raw = ctx.Request.QueryString[key];
        return raw is not null &&
               (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 读请求体。HttpListener 在 Content-Type 没写 charset 时会退回**系统 ANSI 代码页**
    /// （中文系统是 GBK），UTF-8 的中文就被解成「鏍规湰」这种乱码。
    /// 这里默认按 UTF-8 解，只有客户端明确声明了别的 charset 才照它说的来。
    /// </summary>
    private static string ReadBody(HttpListenerContext ctx)
    {
        try
        {
            var contentType = ctx.Request.ContentType ?? "";
            bool charsetDeclared = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase) >= 0;

            Encoding encoding = Encoding.UTF8;
            if (charsetDeclared)
            {
                try { encoding = ctx.Request.ContentEncoding ?? Encoding.UTF8; }
                catch { encoding = Encoding.UTF8; }
            }

            using var reader = new StreamReader(ctx.Request.InputStream, encoding);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    private static string? JsonString(string body, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
        }
        catch { }
        return null;
    }

    private static int? JsonInt(string body, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.Number &&
                    prop.Value.TryGetInt32(out int value))
                    return value;
        }
        catch { }
        return null;
    }

    private static double? JsonDouble(string body, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.Number &&
                    prop.Value.TryGetDouble(out double value))
                    return value;
        }
        catch { }
        return null;
    }

    private static bool? JsonBool(string body, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.True) return true;
                if (prop.Value.ValueKind == JsonValueKind.False) return false;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ 杂项

    private static bool? Probe(SettingItem item)
    {
        try { return item.Probe(); }
        catch { return null; }
    }

    private static string Version()
    {
        try
        {
            var asm = typeof(AgentApi).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus > 0 ? info.Substring(0, plus) : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static int SafeInt(Func<int> read)
    {
        try { return read(); } catch { return -1; }
    }

    private static double SafeDouble(Func<double> read)
    {
        try { return read(); } catch { return double.NaN; }
    }

    private static long SafeUptimeSeconds()
    {
        try { return EnvironmentCompat.TickCount64 / 1000; }
        catch (Exception ex)
        {
            // 极端情况下兼容层取不到（理论上不会），退回 32 位计数，别让整个字段变成 -1
            WriteLog("TickCount64 失败: " + ex.GetType().Name + ": " + ex.Message);
            try { return unchecked((uint)Environment.TickCount) / 1000; }
            catch { return -1; }
        }
    }

    /// <summary>
    /// 浮点输出统一走这里：double 直接序列化会写出 12.076000000000001 这种噪声，
    /// 转 decimal 后按位数四舍五入，JSON 才是干净的 12.08。NaN / 无穷返回 null。
    /// </summary>
    private static decimal? Num(double value, int digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;
        return Math.Round((decimal)value, digits, MidpointRounding.AwayFromZero);
    }
}
