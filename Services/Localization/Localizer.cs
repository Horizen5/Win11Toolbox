using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Win11Toolbox.Services.Localization;

/// <summary>设置页下拉框里的一项。Native 用该语言自己的写法，不参与翻译。</summary>
public sealed record LanguageOption(string Code, string Native, string English);

/// <summary>
/// 运行时文本本地化引擎。
///
/// 为什么要这么做：这个项目的中文文案散在 11 个页面的 XAML 属性和几百处 C# 字面量里，
/// 逐个改成 Binding / x:Uid 要动上千处，还会把「改前必备份」「按需提权」这些已经
/// 验证过的功能一起搅乱。这里改成在元素加载时按「中文原文 → 译文」查表替换文本，
/// 现有 XAML / 业务代码一行都不用改，静态文案和运行时生成的磁贴、卡片、对话框都覆盖得到。
///
/// 三个关键设计：
///  1. 字典以中文原文为 key，所以不需要给每个控件起名字；
///  2. 每个元素用附加属性记住「原文」和「引擎上次写入的值」，
///     业务代码后来改了 Text 时能识别出来，切换语言也不会拿译文去查表；
///  3. 注册 FrameworkElement.LoadedEvent 的类处理器，动态创建的控件自动生效。
/// </summary>
public static class Localizer
{
    private const string ResourceName = "Win11Toolbox.Localization.locales.json";

    /// <summary>需要检查的字符串型属性。object 型（Content / ToolTip）只在值为 string 时处理。</summary>
    private static readonly string[] SlotNames =
    {
        "Text", "Content", "Title", "Header", "PlaceholderText", "Watermark",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText",
        "OffContent", "OnContent", "Message", "Description", "ToolTip",
    };

    private static readonly Dictionary<Type, List<(string Name, PropertyInfo Info)>> SlotCache = new();
    private static readonly object Gate = new();

    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static Dictionary<string, Dictionary<string, string>> _all = new(StringComparer.OrdinalIgnoreCase);
    private static List<LanguageOption> _languages = new();
    private static string _configured = "auto";
    private static string _resolved = "zh-CN";
    private static bool _loaded;
    private static bool _hooked;

    /// <summary>下拉框数据源：自动（跟随系统）+ 十种语言。</summary>
    public static IReadOnlyList<LanguageOption> Languages => _languages;

    /// <summary>配置里存的值（auto / zh-CN / en-US …）。</summary>
    public static string ConfiguredCode => _configured;

    /// <summary>实际生效的语言代码。</summary>
    public static string CurrentCode => _resolved;

    /// <summary>当前语言已翻译的词条数。</summary>
    public static int TranslatedCount => _map.Count;

    /// <summary>切换语言后触发。主窗口订阅后重新扫描整棵可视化树。</summary>
    public static event Action? LanguageChanged;

    // ------------------------------------------------------------------ 附加属性

    private sealed class SlotState
    {
        public readonly Dictionary<string, (string Original, string Last)> Slots = new(StringComparer.Ordinal);
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "LocalizerState", typeof(SlotState), typeof(Localizer), new PropertyMetadata(null));

    private static SlotState GetState(DependencyObject obj)
    {
        var state = (SlotState?)obj.GetValue(StateProperty);
        if (state is null)
        {
            state = new SlotState();
            obj.SetValue(StateProperty, state);
        }
        return state;
    }

    /// <summary>标记后整棵子树都不翻译（语言下拉框自己的原生名要保留）。</summary>
    public static readonly DependencyProperty SkipProperty = DependencyProperty.RegisterAttached(
        "Skip", typeof(bool), typeof(Localizer), new PropertyMetadata(false));

    public static void SetSkip(DependencyObject obj, bool value) => obj.SetValue(SkipProperty, value);
    public static bool GetSkip(DependencyObject obj) => (bool)obj.GetValue(SkipProperty);

    // ------------------------------------------------------------------ 初始化

    /// <summary>在 App.OnStartup 里、窗口创建之前调用一次。</summary>
    public static void Initialize()
    {
        LoadDictionaries();
        _configured = Sanitize(AppConfig.Current.Language);
        ApplyCulture(Resolve(_configured));
        HookLoaded();
    }

    /// <summary>切换语言：落盘 → 重读映射 → 切 UI 文化 → 通知界面重扫。</summary>
    public static void SetLanguage(string code)
    {
        var next = Sanitize(code);
        if (next == _configured)
            return;

        _configured = next;
        AppConfig.Remember(c => c.Language = next);
        ApplyCulture(Resolve(next));
        LanguageChanged?.Invoke();
    }

    /// <summary>只重读当前语言的映射（外部覆盖文件被手工改过时可用）。</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _loaded = false;
        }
        LoadDictionaries();
        ApplyCulture(Resolve(_configured));
        LanguageChanged?.Invoke();
    }

    private static string Sanitize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "auto";
        return code.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : _languages.Any(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) ? code : "auto";
    }

    private static string Resolve(string configured)
    {
        if (configured != "auto") return configured;

        var ui = CultureInfo.CurrentUICulture.Name;
        if (_languages.Any(l => l.Code.Equals(ui, StringComparison.OrdinalIgnoreCase))) return ui;

        // 香港 / 澳门的系统区域走繁体中文，比简体更贴近
        if (ui.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            ui.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
            ui.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase))
            return "zh-TW";

        var prefix = ui.Split('-')[0];
        var match = _languages.FirstOrDefault(l =>
            l.Code.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? "zh-CN";
    }

    private static void ApplyCulture(string code)
    {
        _resolved = code;
        _map = _all.TryGetValue(code, out var dict)
            ? dict
            : new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            // 只改 UICulture，不动 CurrentCulture —— 数字、日期、温度格式仍跟随系统设置，
            // 免得切个语言把「12.5 GB」变成「12,5 GB」这种业务数据也改了。
            var culture = new CultureInfo(code);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch
        {
            // 某些精简系统缺 culture 数据，忽略即可，翻译照常工作
        }
    }

    private static void LoadDictionaries()
    {
        lock (Gate)
        {
            if (_loaded) return;

            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    Parse(reader.ReadToEnd());
                }
            }
            catch
            {
                // 资源缺失时退回「全部保持原文」，绝不因为本地化让程序起不来
            }

            // 用户可以在配置目录放 locales-override.json 补词条，不用重新编译
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Win11Toolbox", "locales-override.json");
                if (File.Exists(path))
                    Parse(File.ReadAllText(path, Encoding.UTF8), merge: true);
            }
            catch
            {
                // 覆盖文件坏了就当没有
            }

            _loaded = true;
        }
    }

    private static void Parse(string json, bool merge = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!merge && root.TryGetProperty("languages", out var langs))
        {
            var list = new List<LanguageOption>();
            foreach (var item in langs.EnumerateArray())
            {
                var code = item.GetProperty("code").GetString() ?? "";
                var native = item.GetProperty("native").GetString() ?? code;
                var english = item.TryGetProperty("english", out var en) ? en.GetString() ?? "" : "";
                list.Add(new LanguageOption(code, native, english));
            }
            if (list.Count > 0) _languages = list;
        }

        if (!root.TryGetProperty("translations", out var translations)) return;

        foreach (var lang in translations.EnumerateObject())
        {
            if (!_all.TryGetValue(lang.Name, out var dict))
            {
                dict = new Dictionary<string, string>(StringComparer.Ordinal);
                _all[lang.Name] = dict;
            }
            foreach (var entry in lang.Value.EnumerateObject())
            {
                var value = entry.Value.GetString();
                if (!string.IsNullOrEmpty(value))
                    dict[entry.Name] = value;
            }
        }
    }

    // ------------------------------------------------------------------ 翻译

    /// <summary>把中文原文翻成当前语言；没收录就原样返回。</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return _map.TryGetValue(text, out var value) ? value : text;
    }

    // ------------------------------------------------------------------ 元素处理

    private static void HookLoaded()
    {
        if (_hooked) return;
        _hooked = true;

        // Loaded 会从元素一路冒泡到根，所以只处理 e.OriginalSource 自己，
        // 否则整棵树会被扫 O(n²) 次。子树的全量扫描交给 Apply()。
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, e) =>
            {
                if (!ReferenceEquals(sender, e.OriginalSource)) return;
                if (sender is FrameworkElement fe) LocalizeElement(fe);
            }));
    }

    /// <summary>扫描一个窗口 / 页面及其整棵可视化树。切换语言、导航切页时调用。</summary>
    public static void Apply(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(RefComparer.Instance);
        Walk(root, seen);
    }

    private static void Walk(DependencyObject node, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(node)) return;

        // Skip 标记作用于整棵子树
        if (node is FrameworkElement skipped && GetSkip(skipped)) return;

        if (node is FrameworkElement fe)
            LocalizeElement(fe);

        try
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(node, i) is DependencyObject child)
                    Walk(child, seen);
            }
        }
        catch { /* 模板还没套上时拿不到可视树，忽略 */ }

        // 逻辑树补漏：菜单项、未展开的 TabItem 内容等可能不在可视树里
        try
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject d) Walk(d, seen);
            }
        }
        catch { /* 同上 */ }
    }

    /// <summary>只处理单个元素，供全局 Loaded 钩子调用。</summary>
    public static void LocalizeElement(FrameworkElement fe)
    {
        if (GetSkip(fe)) return;

        // 输入框里用户真正输入的内容绝对不能翻译
        bool isEditable = fe is TextBox or PasswordBox or RichTextBox;

        foreach (var (name, info) in GetSlots(fe.GetType()))
        {
            if (isEditable && name is "Text" or "Password")
                continue;

            try
            {
                var current = info.GetValue(fe) as string;
                // 空值一律跳过：object 型属性（Content / ToolTip / Header）为 null 时
                // 不能写成空字符串，否则会悄悄改变控件的模板行为
                if (string.IsNullOrEmpty(current))
                    continue;

                LocalizeSlot(fe, name, current, value => info.SetValue(fe, value));
            }
            catch
            {
                // 个别属性是只读的或 setter 会抛，跳过不影响其它文案
            }
        }

        // TextBlock 的富文本 Run 不在可视化树里，Loaded 也轮不到，这里顺手扫一遍
        if (fe is TextBlock tb && tb.Inlines.Count > 0)
            foreach (var inline in tb.Inlines)
                WalkInline(inline);
    }

    private static void WalkInline(Inline inline)
    {
        if (inline is Run run)
        {
            var current = run.Text;
            if (!string.IsNullOrEmpty(current))
                LocalizeSlot(run, "Text", current, value => run.Text = value);
        }
        else if (inline is Span span)
        {
            foreach (var child in span.Inlines)
                WalkInline(child);
        }
    }

    private static void LocalizeSlot(DependencyObject obj, string slot, string? current, Action<string?> set)
    {
        var state = GetState(obj);
        if (!state.Slots.TryGetValue(slot, out var pair))
        {
            // 第一次见：源码里的文本就是中文原文
            pair = (current ?? "", current ?? "");
        }
        else if (!string.Equals(current, pair.Last, StringComparison.Ordinal))
        {
            // 业务代码后来改过文本 → 把它当作新的原文，避免拿旧译文去查字典
            pair = (current ?? "", current ?? "");
        }

        var next = T(pair.Original);
        if (!string.Equals(current, next, StringComparison.Ordinal))
            set(next);

        state.Slots[slot] = (pair.Original, next ?? "");
    }

    private static List<(string Name, PropertyInfo Info)> GetSlots(Type type)
    {
        lock (SlotCache)
        {
            if (SlotCache.TryGetValue(type, out var cached)) return cached;

            var list = new List<(string, PropertyInfo)>();
            foreach (var name in SlotNames)
            {
                var pi = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (pi is null || !pi.CanRead || !pi.CanWrite) continue;
                if (pi.PropertyType != typeof(string) && pi.PropertyType != typeof(object)) continue;
                if (pi.GetIndexParameters().Length > 0) continue;
                list.Add((name, pi));
            }

            SlotCache[type] = list;
            return list;
        }
    }

    private sealed class RefComparer : IEqualityComparer<DependencyObject>
    {
        public static readonly RefComparer Instance = new();
        public bool Equals(DependencyObject? x, DependencyObject? y) => ReferenceEquals(x, y);
        public int GetHashCode(DependencyObject obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
