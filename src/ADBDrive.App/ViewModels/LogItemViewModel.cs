using System.ComponentModel;
using ADBDrive.Core.Adb;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.Localization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace ADBDrive.ViewModels;

/// <summary>日志行。</summary>
public sealed class LogItemViewModel
{
    public LogItemViewModel(LogEntry entry)
    {
        Timestamp = entry.Timestamp;
        Time = entry.Timestamp.ToString("HH:mm:ss");
        Category = entry.Category;
        Message = entry.Message;
        LevelText = entry.Level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warn => "WRN",
            LogLevel.Error => "ERR",
            _ => "INF",
        };
        LevelBrush = entry.Level switch
        {
            LogLevel.Warn => Brush.Parse("#FBBF24"),
            LogLevel.Error => Brush.Parse("#F87171"),
            LogLevel.Debug or LogLevel.Trace => Brush.Parse("#7C838D"),
            _ => Brush.Parse("#7FB6E8"),
        };
    }

    public DateTime Timestamp { get; }

    public string Time { get; }

    public string LevelText { get; }

    public string Category { get; }

    public string Message { get; }

    public IBrush LevelBrush { get; }

    public string Tooltip => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] {Category}";

    /// <summary>复制到剪贴板时的整行文本（带完整时间戳）。</summary>
    public string ToLine()
    {
        var category = string.IsNullOrWhiteSpace(Category) ? string.Empty : Category + ": ";
        return $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] {category}{Message}";
    }
}

/// <summary>adb devices 里发现但尚未保存的设备。</summary>
public sealed class DiscoveredDeviceViewModel : INotifyPropertyChanged
{
    private readonly AdbDeviceState _state;

    public DiscoveredDeviceViewModel(string serial, string name, AdbDeviceState state, bool isOnline, bool isSaved)
    {
        Serial = serial;
        Name = name;
        _state = state;
        IsOnline = isOnline;
        IsSaved = isSaved;
    }

    public string Serial { get; }

    public string Name { get; }

    /// <summary>状态文字按当前语言渲染（切换语言后调用 <see cref="Refresh"/>）。</summary>
    public string StateText => AdbServerClient.DescribeState(_state);

    public bool IsOnline { get; }

    public bool IsSaved { get; }

    public IAsyncRelayCommand? MountCommand { get; set; }

    public string Display => string.Equals(Name, Serial, StringComparison.Ordinal) ? Serial : $"{Name} · {Serial}";

    public string ActionText => IsSaved ? Loc.T("device.connect") : Loc.T("device.mount");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
