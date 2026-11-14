using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using ADBDrive.Core.Localization;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace ADBDrive.Localization;

/// <summary>
/// 一条词条的可观察包装：语言切换时统一通知，XAML 绑定随之刷新。
/// 同一个 key 全局共用一个实例，绑定不会无谓重建。
/// </summary>
public sealed class LocalizedText : INotifyPropertyChanged
{
    private static readonly Dictionary<string, LocalizedText> Registry = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    // 绑定路径是字符串 "Value"，这里显式保根，避免将来收紧裁剪时把属性裁掉。
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(LocalizedText))]
    static LocalizedText() => Loc.Changed += RefreshAll;

    private LocalizedText(string key) => Key = key;

    public string Key { get; }

    /// <summary>当前语言下的文本。绑定只认这个属性。</summary>
    public string Value => Loc.T(Key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public static LocalizedText For(string key)
    {
        lock (Gate)
        {
            if (!Registry.TryGetValue(key, out var item))
            {
                item = new LocalizedText(key);
                Registry[key] = item;
            }

            return item;
        }
    }

    private static void RefreshAll()
    {
        LocalizedText[] items;
        lock (Gate)
        {
            items = [.. Registry.Values];
        }

        foreach (var item in items)
        {
            item.PropertyChanged?.Invoke(item, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}

/// <summary>
/// XAML 里取本地化文本：<c>Text="{loc:Tr app.title}"</c>。
/// 返回的是一条绑定，所以切换语言时界面立即刷新，不用重启。
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    /// <summary>词条键。</summary>
    public string Key { get; set; } = string.Empty;

    // Avalonia 12 移除了公开的 IBinding，没法手写绑定管道，只能建反射绑定。
    // 用法被限死在「单个简单属性 Value」上，且它已用 DynamicDependency 保根，
    // 所以这两条分析警告对本例是误报：AOT 发布版实测切换语言能正常刷新。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "绑定路径只有一个自有的简单属性 Value，已用 DynamicDependency 保根。")]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "同上：单属性反射绑定，NativeAOT 发布版已实测可用。")]
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding(nameof(LocalizedText.Value))
        {
            Source = LocalizedText.For(Key),
            Mode = BindingMode.OneWay,
        };
    }
}
