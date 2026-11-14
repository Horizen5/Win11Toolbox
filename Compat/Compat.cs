// ================================================================
//  Compat.cs —— 让同一份源码同时编译到 .NET 10 和 .NET Framework 4.8.1
//
//  net481 缺少 .NET Core 才有的几个小 API（Math.Clamp / Convert.ToHexString），
//  以及 C# 9+ 的 record / init / required 依赖的几个编译器标记类型。
//  这里给一份等价实现，两边都调同一份代码，不写两套分支。
// ================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Win11Toolbox.Compat
{
    /// <summary>等价于 .NET Core 的 Math.Clamp，四个常用数值类型全覆盖。</summary>
    internal static class MathCompat
    {
        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;
        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
        public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
        public static decimal Clamp(decimal value, decimal min, decimal max) => value < min ? min : value > max ? max : value;
        public static byte Clamp(byte value, byte min, byte max) => value < min ? min : value > max ? max : value;
        public static ushort Clamp(ushort value, ushort min, ushort max) => value < min ? min : value > max ? max : value;

        /// <summary>等价于 double.IsFinite（.NET Core 3.0 才有）。</summary>
        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>等价于 Convert.ToHexString（大写、无分隔符）。</summary>
    internal static class HexCompat
    {
        const string Digits = "0123456789ABCDEF";

        public static string ToHexString(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = Digits[bytes[i] >> 4];
                chars[i * 2 + 1] = Digits[bytes[i] & 0xF];
            }
            return new string(chars);
        }
    }

    /// <summary>等价于 .NET Core 的 Environment.ProcessId / TickCount64。</summary>
    internal static class EnvironmentCompat
    {
        public static int ProcessId => Process.GetCurrentProcess().Id;

        /// <summary>系统启动至今的毫秒数，和 Environment.TickCount64 同义（不会 49 天溢出）。</summary>
        public static long TickCount64
        {
            get
            {
#if NETFRAMEWORK
                return (long)GetTickCount64Native();
#else
                return Environment.TickCount64;
#endif
            }
        }

#if NETFRAMEWORK
        // 入口点必须显式写：kernel32 里叫 GetTickCount64，不写 EntryPoint 的话
        // P/Invoke 会按方法名去找 GetTickCount64Native，运行时直接 EntryPointNotFoundException。
        [DllImport("kernel32.dll", EntryPoint = "GetTickCount64")]
        static extern ulong GetTickCount64Native();
#endif
    }

    /// <summary>等价于 .NET Core 的 RandomNumberGenerator.GetInt32。</summary>
    internal static class RandomCompat
    {
        public static int GetInt32(int toExclusive) => GetInt32(0, toExclusive);

        public static int GetInt32(int fromInclusive, int toExclusive)
        {
            if (fromInclusive >= toExclusive)
                throw new ArgumentOutOfRangeException(nameof(toExclusive));
#if NETFRAMEWORK
            // 拒绝采样，避免取模带来的分布偏差
            uint range = (uint)(toExclusive - fromInclusive);
            uint limit = uint.MaxValue - (uint.MaxValue % range);
            var buffer = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                while (true)
                {
                    rng.GetBytes(buffer);
                    uint value = BitConverter.ToUInt32(buffer, 0);
                    if (value < limit) return (int)(fromInclusive + (value % range));
                }
            }
#else
            return System.Security.Cryptography.RandomNumberGenerator.GetInt32(fromInclusive, toExclusive);
#endif
        }
    }

    /// <summary>Process.Kill(entireProcessTree) 是 .NET Core 3.0 才有的。</summary>
    internal static class ProcessCompat
    {
        public static void KillTree(Process process)
        {
#if NETFRAMEWORK
            process.Kill();
#else
            process.Kill(true);
#endif
        }
    }
}

#if NETFRAMEWORK
// Dictionary.GetValueOrDefault 是 .NET Core 2.0 的扩展方法，这里补一份。
// 只给 net481 定义，否则会和 .NET 10 自带的 CollectionExtensions 撞车。
namespace System.Collections.Generic
{
    internal static class DictionaryCompatExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key)
            => dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
            => dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }
}
#endif

#if NETFRAMEWORK
// net481 没有 Index / Range，C# 8 的 ^1、[a..b] 语法需要这两个类型才能编译。
namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public static Index Start => new Index(0);
        public static Index End => new Index(0, true);
        public static implicit operator Index(int value) => new Index(value);
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object obj) => obj is Index other && Equals(other);
        public override int GetHashCode() => _value;
        public override string ToString() => (IsFromEnd ? "^" : "") + Value.ToString();
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }

        public Range(Index start, Index end) { Start = start; End = end; }

        public static Range All => new Range(Index.Start, Index.End);
        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object obj) => obj is Range other && Equals(other);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
        public override string ToString() => Start + ".." + End;
    }
}

// C# 9/11 的 record、init、required 在编译期会引用这些标记类型，
// .NET 5+ 的运行时自带，.NET Framework 没有，这里补齐（空实现即可）。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) { FeatureName = featureName; }
        public string FeatureName { get; }
        public bool IsOptional { get; set; }
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
        Inherited = false, AllowMultiple = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Constructor, Inherited = false, AllowMultiple = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
#endif
