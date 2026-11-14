using System.ComponentModel;
using System.Globalization;

namespace ADBDrive.Core.Localization;

/// <summary>界面语言。</summary>
public enum AppLanguage
{
    /// <summary>跟随系统（Windows 显示语言）。</summary>
    System,

    /// <summary>简体中文。</summary>
    ChineseSimplified,

    /// <summary>英语。</summary>
    English,
}

/// <summary>配置里保存的语言标识（"system" / "zh-Hans" / "en"）。</summary>
public static class LanguageId
{
    public const string System = "system";
    public const string ChineseSimplified = "zh-Hans";
    public const string English = "en";

    public static AppLanguage Parse(string? id) => (id ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "zh" or "zh-hans" or "zh-cn" or "zh-sg" or "chinese" => AppLanguage.ChineseSimplified,
        "en" or "en-us" or "en-gb" or "english" => AppLanguage.English,
        _ => AppLanguage.System,
    };

    public static string ToId(this AppLanguage language) => language switch
    {
        AppLanguage.ChineseSimplified => ChineseSimplified,
        AppLanguage.English => English,
        _ => System,
    };
}

/// <summary>
/// 轻量本地化器：内置中英两张词表，运行时切换语言不需要重启。
/// 之所以不用 .resx + ResourceManager：AOT 单文件发布下卫星程序集要多带一堆目录，
/// 而这里的词条是编译期常量，裁剪友好、零反射。
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly Lazy<Localizer> LazyInstance = new(() => new Localizer(), isThreadSafe: true);

    private AppLanguage _preference = AppLanguage.System;
    private AppLanguage _current;

    private Localizer() => _current = Resolve(_preference);

    /// <summary>全局唯一实例。</summary>
    public static Localizer Instance => LazyInstance.Value;

    /// <summary>用户选择（可能是「跟随系统」）。</summary>
    public AppLanguage Preference => _preference;

    /// <summary>实际生效的语言（不会是 System）。</summary>
    public AppLanguage Current => _current;

    /// <summary>语言变化（用于刷新 XAML 绑定里的索引器 / 文本）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言变化（供 ViewModel 重新计算字符串）。</summary>
    public event Action? LanguageChanged;

    /// <summary>按配置里的标识应用语言。</summary>
    public void Apply(string? id) => Apply(LanguageId.Parse(id));

    public void Apply(AppLanguage preference)
    {
        var resolved = Resolve(preference);
        if (_preference == preference && _current == resolved)
        {
            return;
        }

        _preference = preference;
        _current = resolved;

        // "Item[]" 是 .NET 约定：索引器整体失效，Avalonia 会刷新所有索引器绑定。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    /// <summary>取词条；缺词条时回退到另一种语言，再缺则返回 key 本身（便于发现漏翻）。</summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var primary = _current == AppLanguage.English ? StringTables.En : StringTables.Zh;
        if (primary.TryGetValue(key, out var text))
        {
            return text;
        }

        var fallback = _current == AppLanguage.English ? StringTables.Zh : StringTables.En;
        return fallback.TryGetValue(key, out var other) ? other : key;
    }

    /// <summary>取词条并填参数（模板里用 {0}{1}）。</summary>
    public string Format(string key, params object?[] args)
    {
        var template = Get(key);
        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    /// <summary>
    /// 带数量的词条：英语有单复数（"1 device" / "2 devices"），
    /// 词表里给单数单独的 key（不足时自动退回原 key）。
    /// </summary>
    public string Count(string key, int count)
    {
        if (count == 1)
        {
            var singleKey = key + ".one";
            var single = Get(singleKey);
            if (!string.Equals(single, singleKey, StringComparison.Ordinal))
            {
                return Format(singleKey, count);
            }
        }

        return Format(key, count);
    }

    private static AppLanguage Resolve(AppLanguage preference) => preference switch
    {
        AppLanguage.ChineseSimplified => AppLanguage.ChineseSimplified,
        AppLanguage.English => AppLanguage.English,
        _ => DetectSystem(),
    };

    /// <summary>系统是中文就用中文，其余语言一律落到英语（目前只有这两种词表）。</summary>
    private static AppLanguage DetectSystem()
    {
        try
        {
            var ui = CultureInfo.CurrentUICulture;
            if (ui.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
            {
                return AppLanguage.ChineseSimplified;
            }

            return AppLanguage.English;
        }
        catch
        {
            return AppLanguage.English;
        }
    }
}

/// <summary>静态快捷入口，代码里取词条用 <see cref="T"/> / <see cref="F"/>。</summary>
public static class Loc
{
    public static Localizer Instance => Localizer.Instance;

    public static AppLanguage Current => Localizer.Instance.Current;

    public static AppLanguage Preference => Localizer.Instance.Preference;

    /// <summary>取词条。</summary>
    public static string T(string key) => Localizer.Instance.Get(key);

    /// <summary>取词条并填参数。</summary>
    public static string F(string key, params object?[] args) => Localizer.Instance.Format(key, args);

    /// <summary>带数量的词条（自动处理英文单复数）。</summary>
    public static string Count(string key, int count) => Localizer.Instance.Count(key, count);

    public static void Apply(AppLanguage preference) => Localizer.Instance.Apply(preference);

    public static void Apply(string? id) => Localizer.Instance.Apply(id);

    /// <summary>切换语言后触发。</summary>
    public static event Action? Changed
    {
        add => Localizer.Instance.LanguageChanged += value;
        remove => Localizer.Instance.LanguageChanged -= value;
    }
}
