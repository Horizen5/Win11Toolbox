using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Win11Toolbox.Services;

/// <summary>ATX 主供电轨道。Other 表示 Vcore / VID / AVCC / 待机电压等杂项。</summary>
public enum VoltageRail
{
    V12,
    V5,
    V33,
    Other,
}

/// <summary>
/// FanController 从 LibreHardwareMonitor 直接读到的一个原始电压传感器。
/// Raw 是 Super I/O 的 ADC 值，可能已经被板上的分压电阻降过，不能直接当输出电压看。
/// </summary>
public readonly record struct VoltageSample(string Hardware, string Name, string Identifier, float Raw);

/// <summary>
/// 一条电压读数：原始值 + 板型系数 + 本次运行的统计。
///
/// <para>
/// 传感器的 <c>Identifier</c>（形如 <c>/lpc/nct6799d/0/voltage/4</c>）才是稳定主键 ——
/// LHM 升级可能把 <c>Voltage #5</c> 改成别的名字，但通道索引不会变。
/// </para>
/// </summary>
public sealed class VoltageReading
{
    public string Hardware { get; init; } = "";
    public string Name { get; init; } = "";
    public string Identifier { get; init; } = "";
    public VoltageRail Rail { get; init; } = VoltageRail.Other;
    public string Label { get; init; } = "";
    public float Nominal { get; init; }

    /// <summary>板型系数（或 1）。用户没校准时生效。</summary>
    public float DefaultGain { get; init; } = 1f;

    /// <summary>是否套用了内置板型系数。</summary>
    public bool BoardCalibrated { get; init; }

    public float Raw { get; private set; }
    public float Gain { get; private set; } = 1f;

    /// <summary>最终显示值 = 原始 ADC 读数 × 板型分压系数。</summary>
    public float Value => Raw * Gain;

    public float Min { get; private set; }
    public float Max { get; private set; }
    public float Baseline { get; private set; }
    public bool HasBaseline { get; private set; }

    /// <summary>相对本次运行首帧的跌落百分比。绝对精度受 ADC 量化限制，相对变化才可信。</summary>
    public float DropPercent => HasBaseline && Baseline > 0
        ? (Baseline - Value) / Baseline * 100f
        : 0f;

    public bool IsRail => Rail != VoltageRail.Other;
    public float ToleranceLow => Nominal * 0.95f;
    public float ToleranceHigh => Nominal * 1.05f;

    /// <summary>ATX 规范对 +12V / +5V / +3.3V 的容差都是 ±5%。</summary>
    public bool InTolerance => IsRail && HasBaseline
                               && Value >= ToleranceLow && Value <= ToleranceHigh;

    internal void Push(float raw, float gain)
    {
        Raw = raw;
        Gain = gain;

        var value = Value;
        if (!HasBaseline)
        {
            Baseline = value;
            Min = value;
            Max = value;
            HasBaseline = true;
        }
        else
        {
            if (value < Min) Min = value;
            if (value > Max) Max = value;
        }
    }
}

/// <summary>
/// 把 <see cref="FanController.Voltages"/> 的原始 ADC 值整理成可显示的三路轨道 + 杂项电压。
///
/// <para>
/// 设计思路取自对硬件监控软件的架构观察：<b>按传感器身份匹配 → 套乘性校准系数 → 维护
/// Min/Max/基线</b>。实现完全基于本项目已有的 LibreHardwareMonitorLib 传感器管线，
/// 不包含、不分发任何第三方程序的驱动、芯片表、语言文件或反编译代码。
/// </para>
/// <para>
/// 为什么不直接显示 Raw：主板把 +12V / +5V 经分压电阻降到 Super I/O ADC 的 0~4.08 V 量程内，
/// 软件若查不到板型系数就会把 12 V 显示成约 1 V。本机 ASUS TUF GAMING B650M-PLUS WIFI
/// 不在 LHM 0.9.6 的板型表里，所以下面按通道索引补了系数。
/// </para>
/// </summary>
public sealed class VoltageMonitor
{
    private sealed record BoardRule(
        string Chip, int Index, VoltageRail Rail, float Gain, float Nominal, string Label);

    /// <summary>
    /// 板型系数表：只登记「LHM 没命名、但通道索引已实测确认」的情况。
    ///
    /// <para>
    /// 通道归属依据：同为 NCT6799D + 华硕 B650 的 PRIME_B650_PLUS 在 LHM 的
    /// SuperIOHardware.cs 里写明 +5V(1, 4, 1) / +12V(4, 11, 1)，通道索引与本机一致。
    /// </para>
    /// <para>
    /// 系数依据：理论分压比是 12 和 5（上面的 (4,11,1)/(1,4,1) 就是 R1/R2），但板上分压电阻
    /// 有 ±1% 容差，实测值会偏离理论值。这里的 11.98 / 5.02 是用同源 ADC 对照校准出来的：
    /// 同一时刻同一通道，本机原始读数 1.008 V → 12.0758 V、1.000 V → 5.0200 V，
    /// 与专业监控软件对这块主板的换算完全一致。原始值是 8 mV/LSB 的整数倍，
    /// 乘回后 12V 轨步进 0.0958 V，这仍是软件测不破的物理上限。
    /// </para>
    /// </summary>
    private static readonly BoardRule[] BoardRules =
    {
        new("nct6799d", 4, VoltageRail.V12, 11.98f, 12f, "+12V"),
        new("nct6799d", 1, VoltageRail.V5, 5.02f, 5f, "+5V"),
        new("nct6799d", 3, VoltageRail.V33, 1f, 3.3f, "+3.3V"),
    };

    private static readonly Regex IndexPattern = new(
        @"/voltage/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, VoltageReading> _byId = new(StringComparer.Ordinal);

    /// <summary>三路主轨道（可能少于 3 条：没识别到的轨道不硬造数据）。</summary>
    public IReadOnlyList<VoltageReading> Rails => _byId.Values
        .Where(r => r.IsRail)
        .OrderBy(r => (int)r.Rail)
        .ToArray();

    /// <summary>Vcore / VID / AVCC / 待机电压 / GPU 核心电压等。</summary>
    public IReadOnlyList<VoltageReading> Others => _byId.Values
        .Where(r => !r.IsRail)
        .OrderBy(r => r.Hardware, StringComparer.Ordinal)
        .ThenBy(r => r.Identifier, StringComparer.Ordinal)
        .ToArray();

    public int Count => _byId.Count;

    public VoltageReading? Find(VoltageRail rail)
        => _byId.Values.FirstOrDefault(r => r.Rail == rail);

    /// <summary>喂入一次采样。同一 Identifier 的 Min/Max/基线会跨采样保留。</summary>
    public void Update(IReadOnlyList<VoltageSample> samples)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sample in samples)
        {
            if (string.IsNullOrEmpty(sample.Identifier)) continue;
            seen.Add(sample.Identifier);

            if (!_byId.TryGetValue(sample.Identifier, out var reading))
            {
                reading = Create(sample);
                _byId[sample.Identifier] = reading;
            }

            reading.Push(sample.Raw, reading.DefaultGain);
        }

        // 传感器在这一轮消失（驱动重载 / 换过主板）就丢掉旧对象，别一直显示陈旧值
        foreach (var key in _byId.Keys.Where(k => !seen.Contains(k)).ToArray())
            _byId.Remove(key);
    }

    private static VoltageReading Create(VoltageSample sample)
    {
        var (rail, label, nominal, gain, board) = Classify(sample);
        return new VoltageReading
        {
            Hardware = sample.Hardware,
            Name = sample.Name,
            Identifier = sample.Identifier,
            Rail = rail,
            Label = label,
            Nominal = nominal,
            DefaultGain = gain,
            BoardCalibrated = board,
        };
    }

    private static (VoltageRail Rail, string Label, float Nominal, float Gain, bool Board)
        Classify(VoltageSample sample)
    {
        var name = sample.Name ?? "";
        var id = sample.Identifier ?? "";

        // 1) LHM 已经把轨道命名并换算好的板子：直接采信，绝不再乘一次系数。
        if (TryExplicitRail(name, out var native))
            return (native.Rail, native.Label, native.Nominal, 1f, false);

        // 2) 名字还是 "Voltage #N" 这类通用名 → 按芯片 + 通道索引套板型系数。
        //    只对明确登记过的芯片动手，其它主板一律原样列入「其他电压」，不猜。
        var index = ChannelIndex(id);
        if (index >= 0)
        {
            foreach (var rule in BoardRules)
            {
                if (rule.Index == index && id.Contains(rule.Chip, StringComparison.OrdinalIgnoreCase))
                    return (rule.Rail, rule.Label, rule.Nominal, rule.Gain, true);
            }
        }

        return (VoltageRail.Other, name, 0f, 1f, false);
    }

    /// <summary>
    /// 识别 LHM 已经命名的主轨道。必须先排掉 5VSB / VID / AVCC / CPU Termination 这类
    /// 名字里带数字但实际不是 ATX 输出轨的传感器，否则会把待机电压误显示成 +5V。
    /// </summary>
    private static bool TryExplicitRail(string name,
        out (VoltageRail Rail, string Label, float Nominal) result)
    {
        result = default;
        var n = name.Replace(" ", "").ToUpperInvariant();
        if (n.Length == 0) return false;

        string[] excluded =
        {
            "STANDBY", "VSB", "VID", "VCORE", "AVCC", "TERMINATION", "BATTERY", "STBY",
        };
        if (excluded.Any(x => n.Contains(x, StringComparison.Ordinal))) return false;

        if (n.Contains("12V", StringComparison.Ordinal))
        {
            result = (VoltageRail.V12, "+12V", 12f);
            return true;
        }
        if (n.Contains("3.3V", StringComparison.Ordinal) || n.Contains("3V3", StringComparison.Ordinal))
        {
            result = (VoltageRail.V33, "+3.3V", 3.3f);
            return true;
        }
        if (n.Contains("5V", StringComparison.Ordinal))
        {
            result = (VoltageRail.V5, "+5V", 5f);
            return true;
        }
        return false;
    }

    private static int ChannelIndex(string id)
    {
        var match = IndexPattern.Match(id);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var index)
            ? index
            : -1;
    }

    /// <summary>诊断用：把当前所有电压传感器按「原始 → 换算」列一遍（--probe 会写进日志）。</summary>
    public string Dump()
    {
        var lines = new List<string>();
        foreach (var r in _byId.Values.OrderBy(r => r.Identifier, StringComparer.Ordinal))
        {
            var source = r.BoardCalibrated ? "板型系数" : "原样";
            lines.Add($"{r.Identifier,-42} {r.Name,-24} 原始={r.Raw,7:F3} V "
                    + $"×{r.Gain,-5:0.##} = {r.Value,7:F3} V  [{r.Label}] {source}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
