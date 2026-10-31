using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Win11Toolbox.Services;

public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// TOTP（RFC 6238）验证码计算。
///
/// <para>
/// 这里刻意<b>不提供任何持久化入口</b>：没有 Save / Load / Config 字段，
/// 密钥以 byte[] 形式只活在 <see cref="TotpAccount"/> 实例里，
/// 删条目或退出程序时 <see cref="Array.Clear(Array)"/> 抹掉。
/// 用户要求「不要保存密码」，所以连"记住上次密钥"这种便利都不做。
/// </para>
///
/// <para>
/// .NET 没有内置 Base32，Google Authenticator / 各家网站给的都是
/// RFC 4648 的 Base32 字符串（A-Z 2-7），必须自己解。
/// </para>
/// </summary>
public static class Totp
{
    /// <summary>RFC 4648 Base32 解码，容忍空格、连字符、小写和尾部的 = 填充。</summary>
    public static byte[] DecodeBase32(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            if (char.IsWhiteSpace(raw) || raw == '-' || raw == '=')
                continue;

            var c = char.ToUpperInvariant(raw);
            bool valid = (c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7');
            if (!valid)
                throw new FormatException($"密钥里有非法字符「{raw}」，Base32 只允许 A-Z 和 2-7。");

            clean.Append(c);
        }

        if (clean.Length == 0)
            throw new FormatException("密钥是空的。");

        var output = new List<byte>(clean.Length * 5 / 8 + 1);
        int buffer = 0;
        int bits = 0;

        foreach (var c in clean.ToString())
        {
            // 必须显式判 'A'-'Z' 区间：'0'-'9' 的 ASCII 比 'A' 还小，
            // 写成 c <= 'Z' 会把数字当成字母，'6' 变成 -11 —— 解出来的密钥全歪，
            // 但验证码照样跳数字，只有登录时才被拒。--totp 自检就是抓这个的。
            int value = c >= 'A' && c <= 'Z' ? c - 'A' : c - '2' + 26;
            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return output.ToArray();
    }

    /// <summary>按计数器算一次验证码（纯数字，未分组）。</summary>
    public static string Compute(byte[] key, TotpAlgorithm algorithm, long counter, int digits)
    {
        if (key.Length == 0)
            throw new ArgumentException("密钥为空。", nameof(key));

        digits = Math.Clamp(digits, 6, 10);

        Span<byte> message = stackalloc byte[8];
        for (int i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        byte[] hash;
        using (HMAC hmac = algorithm switch
        {
            TotpAlgorithm.Sha256 => new HMACSHA256(key),
            TotpAlgorithm.Sha512 => new HMACSHA512(key),
            _ => new HMACSHA1(key),
        })
        {
            hash = hmac.ComputeHash(message.ToArray());
        }

        // RFC 4226 动态截断：末字节低 4 位当偏移，取 4 字节当大端整数
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);

        long modulo = 1;
        for (int i = 0; i < digits; i++)
            modulo *= 10;

        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>按时间戳算验证码，给自检用。</summary>
    public static string ComputeAt(byte[] key, TotpAlgorithm algorithm, long unixSeconds, int period, int digits)
        => Compute(key, algorithm, unixSeconds / Math.Max(1, period), digits);

    /// <summary>
    /// 解析用户粘贴的内容：可以是裸 Base32 密钥，也可以是 otpauth:// 链接
    /// （很多网站给的二维码里就是这个格式，用户直接从"手动输入"处复制得到）。
    /// </summary>
    public static bool TryParse(string input, out TotpAccount? account, out string error)
    {
        account = null;
        error = "";

        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "先粘贴密钥或 otpauth 链接。";
            return false;
        }

        string secret = text;
        string issuer = "";
        string label = "";
        int digits = 6;
        int period = 30;
        var algorithm = TotpAlgorithm.Sha1;

        if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                error = "otpauth 链接格式不对。";
                return false;
            }

            if (!uri.Host.Equals("totp", StringComparison.OrdinalIgnoreCase))
            {
                error = "这是 HOTP（计数器型）链接，本工具只支持 TOTP（时间型）。";
                return false;
            }

            var query = ParseQuery(uri.Query);

            if (!query.TryGetValue("secret", out var s) || string.IsNullOrWhiteSpace(s))
            {
                error = "链接里没有 secret 参数。";
                return false;
            }

            secret = s;

            if (query.TryGetValue("issuer", out var iss) && !string.IsNullOrWhiteSpace(iss))
                issuer = iss;

            if (query.TryGetValue("digits", out var d) && int.TryParse(d, out var dv))
                digits = Math.Clamp(dv, 6, 10);

            if (query.TryGetValue("period", out var p) && int.TryParse(p, out var pv) && pv > 0)
                period = Math.Clamp(pv, 5, 300);

            if (query.TryGetValue("algorithm", out var a))
            {
                algorithm = a.ToUpperInvariant() switch
                {
                    "SHA256" => TotpAlgorithm.Sha256,
                    "SHA512" => TotpAlgorithm.Sha512,
                    _ => TotpAlgorithm.Sha1,
                };
            }

            // otpauth://totp/Issuer:account —— 路径是 URL 编码的标签
            var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (path.Length > 0)
            {
                label = path;
                var colon = label.IndexOf(':');
                if (colon > 0)
                {
                    if (issuer.Length == 0)
                        issuer = label[..colon].Trim();
                    label = label[(colon + 1)..].Trim();
                }
            }
        }

        byte[] key;
        try
        {
            key = DecodeBase32(secret);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        // 80 位是 RFC 4226 规定的最短密钥，短于此的密钥基本是用户少粘了一段
        if (key.Length < 10)
        {
            Array.Clear(key);
            error = $"密钥太短（{key.Length * 8} 位），至少要 80 位（16 个 Base32 字符）。";
            return false;
        }

        account = new TotpAccount(key, algorithm, digits, period, issuer, label);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[name] = value;
        }

        return result;
    }
}

/// <summary>
/// 一条 2FA 账户。<b>只存在于内存</b>，密钥用 <see cref="IDisposable"/> 负责擦除。
/// </summary>
public sealed class TotpAccount : INotifyPropertyChanged, IDisposable
{
    private readonly byte[] _key;
    private long _counter = long.MinValue;

    private string _code = "";
    private string _rawCode = "";
    private int _remaining;
    private double _progress = 1;

    public TotpAccount(byte[] key, TotpAlgorithm algorithm, int digits, int period, string issuer, string label)
    {
        _key = key;
        Algorithm = algorithm;
        Digits = digits;
        Period = period;
        Issuer = issuer;
        Label = string.IsNullOrWhiteSpace(label) ? (string.IsNullOrWhiteSpace(issuer) ? "未命名账户" : issuer) : label;

        var name = algorithm switch
        {
            TotpAlgorithm.Sha256 => "SHA256",
            TotpAlgorithm.Sha512 => "SHA512",
            _ => "SHA1",
        };
        Meta = $"{name} · {digits} 位 · {period} 秒";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; private set; }
    public string Issuer { get; }
    public string Meta { get; }
    public TotpAlgorithm Algorithm { get; }
    public int Digits { get; }
    public int Period { get; }

    /// <summary>显示用验证码，中间带一个空格（123 456）。</summary>
    public string Code
    {
        get => _code;
        private set => Set(ref _code, value);
    }

    /// <summary>复制用验证码，纯数字。</summary>
    public string RawCode
    {
        get => _rawCode;
        private set => Set(ref _rawCode, value);
    }

    public int Remaining
    {
        get => _remaining;
        private set => Set(ref _remaining, value);
    }

    /// <summary>剩余比例 1→0，倒计时圆圈用。</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>用户自己填了备注时改名（不改密钥，不重建对象，避免多留一份密钥副本）。</summary>
    public void Rename(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label == Label)
            return;

        Label = label.Trim();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

    /// <summary>每次界面刷新（200ms 一次）调用：跨周期时重算验证码。</summary>
    public void Tick(DateTimeOffset now)
    {
        long seconds = now.ToUnixTimeSeconds();
        long counter = seconds / Period;

        if (counter != _counter)
        {
            _counter = counter;
            var raw = Totp.Compute(_key, Algorithm, counter, Digits);
            RawCode = raw;
            Code = Group(raw);
        }

        // 周期内的位置：秒 + 毫秒，避免整秒跳变时圆圈一顿一顿的
        double elapsed = (seconds % Period) + now.Millisecond / 1000.0;
        double left = Math.Max(0, 1 - elapsed / Period);

        Progress = left;

        int remaining = (int)Math.Ceiling(Period - elapsed);
        Remaining = Math.Clamp(remaining, 0, Period);
    }

    private static string Group(string raw)
    {
        int half = raw.Length / 2;
        return raw.Length <= 4 ? raw : raw[..half] + " " + raw[half..];
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>把密钥字节清零。删条目 / 关页面 / 退出程序都会走到这里。</summary>
    public void Dispose()
    {
        Array.Clear(_key);
        RawCode = "";
        Code = "";
    }
}
