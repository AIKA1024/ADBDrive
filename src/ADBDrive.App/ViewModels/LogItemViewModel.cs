using ADBDrive.Core.Diagnostics;
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
}

/// <summary>adb devices 里发现但尚未保存的设备。</summary>
public sealed class DiscoveredDeviceViewModel
{
    public DiscoveredDeviceViewModel(string serial, string name, string stateText, bool isOnline, bool isSaved)
    {
        Serial = serial;
        Name = name;
        StateText = stateText;
        IsOnline = isOnline;
        IsSaved = isSaved;
    }

    public string Serial { get; }

    public string Name { get; }

    public string StateText { get; }

    public bool IsOnline { get; }

    public bool IsSaved { get; }

    public IAsyncRelayCommand? MountCommand { get; set; }

    public string Display => string.Equals(Name, Serial, StringComparison.Ordinal) ? Serial : $"{Name} · {Serial}";

    public string ActionText => IsSaved ? "连接" : "挂载";
}
