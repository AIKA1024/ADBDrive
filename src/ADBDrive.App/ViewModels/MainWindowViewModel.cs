using System.Collections.ObjectModel;
using System.Diagnostics;
using ADBDrive.Core.Adb;
using ADBDrive.Core.Config;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.Mount;
using ADBDrive.Core.Sessions;
using ADBDrive.Services;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADBDrive.ViewModels;

/// <summary>主窗口视图模型。</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int MaxLogItems = 900;
    private const int MaxLogBuffer = 3000;

    private readonly AppHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly List<DeviceSession> _knownSessions = [];
    private readonly List<LogEntry> _logBuffer = [];
    private bool _refreshing;

    [ObservableProperty]
    private string _adbStatus = "adb 检测中…";

    [ObservableProperty]
    private IBrush _adbStatusBrush = Brush.Parse("#6E747D");

    [ObservableProperty]
    private string _dokanStatus = "Dokan 检测中…";

    /// <summary>Dokan 的完整状态（含库/驱动版本），用于徽标的悬停提示。</summary>
    [ObservableProperty]
    private string _dokanDetail = string.Empty;

    [ObservableProperty]
    private IBrush _dokanStatusBrush = Brush.Parse("#6E747D");

    [ObservableProperty]
    private bool _dokanMissing;

    [ObservableProperty]
    private string _newEndpoint = string.Empty;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newRoot = "/sdcard";

    [ObservableProperty]
    private string _newDrive = "自动";

    [ObservableProperty]
    private bool _newAutoConnect = true;

    [ObservableProperty]
    private bool _newAutoMount = true;

    [ObservableProperty]
    private bool _isLogVisible = true;

    [ObservableProperty]
    private string _deviceCountText = "暂无设备";

    [ObservableProperty]
    private string? _bannerMessage;

    [ObservableProperty]
    private bool _bannerVisible;

    [ObservableProperty]
    private string _discoveredCountText = "—";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasDevices;

    [ObservableProperty]
    private string _selectedLogLevel = "信息及以上";

    public MainWindowViewModel(AppHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.UIThread;

        DriveLetterOptions = ["自动"];
        var used = DriveLetters.GetUsed();
        foreach (var letter in "DEFGHIJKLMNOPQRSTUVWXYZ")
        {
            if (!used.Contains(letter))
            {
                DriveLetterOptions.Add($"{letter}:");
            }
        }

        UpdateStatusChips();
        _host.Manager.SessionsChanged += () => _dispatcher.Post(RefreshSessions);
        _host.Log.EntryWritten += entry => _dispatcher.Post(() => AppendLog(entry));

        foreach (var entry in _host.Log.Snapshot())
        {
            AppendLog(entry, trim: false);
        }

        RefreshSessions();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public ObservableCollection<DiscoveredDeviceViewModel> Discovered { get; } = [];

    public ObservableCollection<LogItemViewModel> Logs { get; } = [];

    public ObservableCollection<string> DriveLetterOptions { get; }

    public ObservableCollection<string> LogLevelOptions { get; } = ["信息及以上", "全部", "仅警告", "仅错误"];

    public string AdbPathText => _host.AdbPath ?? "未找到 adb.exe";

    public string ConfigPathText => _host.ConfigPath;

    public string LogDirectoryText => _host.Log.LogDirectory ?? "未启用";

    /// <summary>日志滚动到末尾的请求。</summary>
    public event Action? LogAdded;

    private void UpdateStatusChips()
    {
        if (_host.AdbFound)
        {
            AdbStatus = "adb 就绪";
            AdbStatusBrush = Brush.Parse("#4ADE80");
        }
        else
        {
            AdbStatus = "未找到 adb";
            AdbStatusBrush = Brush.Parse("#F87171");
        }

        DokanMissing = !_host.DokanInstalled;
        DokanStatus = _host.DokanInstalled ? "Dokan 就绪" : "Dokan 未安装";
        DokanDetail = _host.DokanStatus;
        DokanStatusBrush = _host.DokanInstalled ? Brush.Parse("#4ADE80") : Brush.Parse("#FBBF24");

        if (!_host.DokanInstalled)
        {
            BannerMessage = $"{_host.DokanStatus}。安装 Dokan 驱动后才能把设备挂载成盘符。";
            BannerVisible = true;
        }
    }

    public void RefreshSessions()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            foreach (var session in _host.Manager.Sessions)
            {
                if (_knownSessions.Contains(session))
                {
                    continue;
                }

                _knownSessions.Add(session);
                var vm = new DeviceViewModel(session, OnSessionActionAsync);
                vm.RemoveRequested += OnRemoveRequested;
                Devices.Add(vm);
            }

            foreach (var vm in Devices.ToArray())
            {
                if (!_knownSessions.Contains(vm.Session))
                {
                    Devices.Remove(vm);
                }
            }

            foreach (var vm in Devices)
            {
                vm.Refresh();
            }

            DeviceCountText = Devices.Count == 0 ? "暂无设备" : $"{Devices.Count} 台设备";
            HasDevices = Devices.Count > 0;
        }
        finally
        {
            _refreshing = false;
        }

        UpdateStatusChips();
    }

    private void OnRemoveRequested(DeviceViewModel vm)
    {
        _knownSessions.Remove(vm.Session);
        _host.Manager.RemoveDevice(vm.Session);
        _dispatcher.Post(RefreshSessions);
    }

    private async Task OnSessionActionAsync()
    {
        _host.Manager.SaveConfig();
        _dispatcher.Post(RefreshSessions);
        await Task.CompletedTask;
    }

    private void AppendLog(LogEntry entry, bool trim = true)
    {
        _logBuffer.Add(entry);
        while (_logBuffer.Count > MaxLogBuffer)
        {
            _logBuffer.RemoveAt(0);
        }

        if (!PassesFilter(entry))
        {
            return;
        }

        Logs.Add(new LogItemViewModel(entry));
        while (trim && Logs.Count > MaxLogItems)
        {
            Logs.RemoveAt(0);
        }

        LogAdded?.Invoke();
    }

    partial void OnSelectedLogLevelChanged(string value) => RebuildLogs();

    private void RebuildLogs()
    {
        Logs.Clear();
        foreach (var entry in _logBuffer.Where(PassesFilter).TakeLast(MaxLogItems))
        {
            Logs.Add(new LogItemViewModel(entry));
        }

        LogAdded?.Invoke();
    }

    private bool PassesFilter(LogEntry entry) => SelectedLogLevel switch
    {
        "全部" => true,
        "仅警告" => entry.Level >= LogLevel.Warn,
        "仅错误" => entry.Level >= LogLevel.Error,
        _ => entry.Level >= LogLevel.Info,
    };

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshDiscoveredAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshDiscoveredAsync()
    {
        try
        {
            if (!await _host.Server.EnsureServerAsync(CancellationToken.None))
            {
                DiscoveredCountText = "adb server 不可用";
                return;
            }

            var devices = await _host.Server.ListDevicesAsync(CancellationToken.None);
            var saved = _host.Manager.Sessions.Select(s => s.Endpoint).ToHashSet(StringComparer.OrdinalIgnoreCase);

            Discovered.Clear();
            foreach (var device in devices)
            {
                var item = new DiscoveredDeviceViewModel(
                    device.Serial,
                    device.Model ?? device.Device ?? device.Serial,
                    AdbServerClient.DescribeState(device.State),
                    device.IsOnline,
                    saved.Contains(device.Serial));
                item.MountCommand = new AsyncRelayCommand(() => MountDiscoveredAsync(item));
                Discovered.Add(item);
            }

            DiscoveredCountText = Discovered.Count == 0 ? "没有发现设备" : $"{Discovered.Count} 台在线/离线设备";
        }
        catch (Exception ex)
        {
            DiscoveredCountText = "读取设备列表失败";
            _host.Log.Warn("ADB", $"读取设备列表失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddDeviceAsync()
    {
        var endpoint = NormalizeEndpoint(NewEndpoint);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            BannerMessage = "请填写设备地址，例如 192.168.1.20:5555";
            BannerVisible = true;
            return;
        }

        var root = string.IsNullOrWhiteSpace(NewRoot) ? "/sdcard" : NewRoot.Trim();
        if (!root.StartsWith('/'))
        {
            root = "/" + root;
        }

        var config = new DeviceConfig
        {
            Endpoint = endpoint,
            DisplayName = string.IsNullOrWhiteSpace(NewName) ? null : NewName.Trim(),
            RootPath = root,
            DriveLetter = NewDrive == "自动" || NewDrive.Length == 0 ? null : NewDrive[..1],
            AutoConnect = NewAutoConnect,
            AutoMount = NewAutoMount,
        };

        await AddAndMountAsync(config);
        NewEndpoint = string.Empty;
        NewName = string.Empty;
    }

    private async Task AddAndMountAsync(DeviceConfig config)
    {
        IsBusy = true;
        try
        {
            BannerVisible = false;
            var session = _host.Manager.AddDevice(config);
            RefreshSessions();
            await session.ConnectAsync();

            if (session.State == SessionState.Connected && config.AutoMount)
            {
                await session.MountAsync();
            }
            else if (session.State == SessionState.Unauthorized)
            {
                BannerMessage = "设备未授权：请解锁手机并在弹窗中点击「允许」。之后会自动继续。";
                BannerVisible = true;
            }
            else if (session.State is SessionState.Error or SessionState.Offline)
            {
                BannerMessage = session.StatusMessage;
                BannerVisible = true;
            }
        }
        catch (Exception ex)
        {
            BannerMessage = ex.Message;
            BannerVisible = true;
            _host.Log.Error("会话", "添加设备失败", ex);
        }
        finally
        {
            IsBusy = false;
            _host.Manager.SaveConfig();
            RefreshSessions();
            await RefreshDiscoveredAsync();
        }
    }

    [RelayCommand]
    private async Task MountDiscoveredAsync(DiscoveredDeviceViewModel? device)
    {
        if (device is null)
        {
            return;
        }

        var existing = _host.Manager.Sessions.FirstOrDefault(s =>
            string.Equals(s.Endpoint, device.Serial, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await existing.ConnectAsync();
            if (existing.State == SessionState.Connected)
            {
                await existing.MountAsync();
            }

            RefreshSessions();
            return;
        }

        await AddAndMountAsync(new DeviceConfig
        {
            Endpoint = device.Serial,
            RootPath = "/sdcard",
            AutoConnect = true,
            AutoMount = true,
        });
    }

    [RelayCommand]
    private void DismissBanner() => BannerVisible = false;

    [RelayCommand]
    private void ToggleLog() => IsLogVisible = !IsLogVisible;

    /// <summary>点击预设路径（来自「添加设备」里根路径旁的下拉菜单）。</summary>
    [RelayCommand]
    private void PickNewRoot(string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            NewRoot = root;
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _logBuffer.Clear();
        Logs.Clear();
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var directory = _host.Log.LogDirectory;
            if (!string.IsNullOrEmpty(directory))
            {
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _host.Log.Warn("界面", $"打开日志目录失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenDokanDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/dokan-dev/dokany/releases/latest") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Log.Warn("界面", $"打开下载页失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RecheckDokan()
    {
        var installed = DokanSupport.IsInstalled(out var message);
        DokanMissing = !installed;
        DokanStatus = installed ? "Dokan 就绪" : "Dokan 未安装";
        DokanDetail = message;
        DokanStatusBrush = installed ? Brush.Parse("#4ADE80") : Brush.Parse("#FBBF24");
        if (installed)
        {
            BannerVisible = false;
        }
        else
        {
            BannerMessage = message;
            BannerVisible = true;
        }
    }

    public void Shutdown()
    {
        _host.Manager.SaveConfig();
    }

    private static string NormalizeEndpoint(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 没写端口时补默认 5555（无线调试默认端口）。
        if (!text.Contains(':') && text.Count(c => c == '.') == 3)
        {
            text += ":5555";
        }

        return text;
    }
}
