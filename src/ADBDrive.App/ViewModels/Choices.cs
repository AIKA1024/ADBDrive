using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using ADBDrive.Core.Localization;
using ADBDrive.Core.Mount;

namespace ADBDrive.ViewModels;

/// <summary>日志级别过滤。</summary>
public enum LogLevelFilter
{
    Info,
    All,
    Warnings,
    Errors,
}

/// <summary>
/// 盘符下拉项。字母是固定的，只有「自动 / Auto」这一项的文字随语言变化，
/// 所以单独做成可观察对象，切换语言时原地刷新即可，不会打断 ComboBox 的选中状态。
/// </summary>
public sealed class DriveOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public DriveOption(char? letter) => Letter = letter;

    /// <summary>盘符；null 表示自动分配。</summary>
    public char? Letter { get; }

    public bool IsAuto => Letter is null;

    public string Label => Letter is null ? Loc.T("drive.auto") : $"{Letter}:";

    /// <summary>是不是当前选中的盘符（右键菜单里打勾）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>选中自己；由所属的设备视图模型注入，菜单项直接绑这个命令。</summary>
    public ICommand? SelectCommand { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    public override string ToString() => Label;

    /// <summary>「自动」+ 当前空闲的盘符（D 到 Z）。</summary>
    public static ObservableCollection<DriveOption> CreateAll()
    {
        var options = new ObservableCollection<DriveOption> { new(null) };
        var used = DriveLetters.GetUsed();
        foreach (var letter in "DEFGHIJKLMNOPQRSTUVWXYZ")
        {
            if (!used.Contains(letter))
            {
                options.Add(new DriveOption(letter));
            }
        }

        return options;
    }
}

/// <summary>
/// 语言下拉项。实例固定不变，只有「跟随系统」这类标签会随语言变，
/// 所以原地刷新即可 —— 不能重建集合，否则用户正在操作的下拉会被打断。
/// </summary>
public sealed class LanguageOption : INotifyPropertyChanged
{
    public LanguageOption(AppLanguage value) => Value = value;

    public AppLanguage Value { get; }

    public string Label => Value switch
    {
        AppLanguage.ChineseSimplified => Loc.T("language.zh"),
        AppLanguage.English => Loc.T("language.en"),
        _ => Loc.T("language.system"),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));

    public override string ToString() => Label;

    public static List<LanguageOption> CreateAll() =>
    [
        new(AppLanguage.System),
        new(AppLanguage.ChineseSimplified),
        new(AppLanguage.English),
    ];
}

/// <summary>日志级别下拉项。</summary>
public sealed record LogLevelOption(LogLevelFilter Value, string Label)
{
    public static List<LogLevelOption> CreateAll() =>
    [
        new(LogLevelFilter.Info, Loc.T("logs.level.info")),
        new(LogLevelFilter.All, Loc.T("logs.level.all")),
        new(LogLevelFilter.Warnings, Loc.T("logs.level.warn")),
        new(LogLevelFilter.Errors, Loc.T("logs.level.error")),
    ];
}
