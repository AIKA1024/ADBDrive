using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using ADBDrive.Core.Adb;
using ADBDrive.Core.Config;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.Localization;
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

    private static readonly FontFamily ChineseFont = new("Microsoft YaHei UI, Segoe UI");
    private static readonly FontFamily EnglishFont = new("Segoe UI Variable Text, Segoe UI");

    private readonly AppHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly List<DeviceSession> _knownSessions = [];

    private readonly List<LogEntry> _logBuffer = [];
    private bool _refreshing;

    [ObservableProperty]
    private string _adbStatus = string.Empty;

    [ObservableProperty]
    private IBrush _adbStatusBrush = Brush.Parse("#6E747D");

    [ObservableProperty]
    private string _dokanStatus = string.Empty;

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
    private DriveOption? _newDrive;

    [ObservableProperty]
    private bool _newAutoConnect = true;

    [ObservableProperty]
    private bool _newAutoMount = true;

    [ObservableProperty]
    private bool _isLogVisible = true;

    [ObservableProperty]
    private string _deviceCountText = string.Empty;

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
    private LogLevelOption? _selectedLogLevel;

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    /// <summary>关窗口时收进托盘（设置面板可改）。</summary>
    [ObservableProperty]
    private bool _minimizeToTray = true;

    /// <summary>启动时直接最小化到托盘（设置面板可改）。</summary>
    [ObservableProperty]
    private bool _startMinimized;

    public MainWindowViewModel(AppHost host)
    {
        _host = host;
        _dispatcher = Dispatcher.UIThread;

        DriveLetterOptions = DriveOption.CreateAll();
        NewDrive = DriveLetterOptions.FirstOrDefault();

        _minimizeToTray = host.Config.MinimizeToTray;
        _startMinimized = host.Config.StartMinimized;

        RebuildLogLevelOptions(null);
        BuildLanguageOptions();

        UpdateStatusChips();
        UpdatePathTexts();
        _host.Manager.SessionsChanged += () => _dispatcher.Post(RefreshSessions);
        _host.Log.EntryWritten += entry => _dispatcher.Post(() => AppendLog(entry));

        Loc.Changed += OnLanguageChanged;

        foreach (var entry in _host.Log.Snapshot())
        {
            AppendLog(entry, trim: false);
        }

        RefreshSessions();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public ObservableCollection<DiscoveredDeviceViewModel> Discovered { get; } = [];

    public ObservableCollection<LogItemViewModel> Logs { get; } = [];

    public ObservableCollection<DriveOption> DriveLetterOptions { get; }

    public ObservableCollection<LogLevelOption> LogLevelOptions { get; } = [];

    public ObservableCollection<LanguageOption> LanguageOptions { get; } = [];

    /// <summary>中英文用各自的系统字体，看看英文下 Segoe UI 的字形比雅黑更自然。</summary>
    public FontFamily UiFontFamily => Loc.Current == AppLanguage.English ? EnglishFont : ChineseFont;

    public string AdbPathText => _host.AdbPath ?? Loc.T("status.adbPathMissing");

    public string ConfigPathText => _host.ConfigPath;

    public string LogDirectoryText => _host.Log.LogDirectory ?? Loc.T("logs.disabled");

    public string ConfigDirectoryText => Path.GetDirectoryName(ConfigPathText) ?? ConfigPathText;

    /// <summary>日志条数（"12 条" / "12 entries"）。</summary>
    public string LogCountText => Loc.Count("logs.count", Logs.Count);

    public string VersionText =>
        "ADBDrive v" + (typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0")
        + " · NativeAOT · Avalonia 12";

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _host.Config.MinimizeToTray = value;
        _host.SaveConfig();
        _host.Log.Info("设置", value ? "关闭窗口时收进托盘" : "关闭窗口时退出程序");
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        _host.Config.StartMinimized = value;
        _host.SaveConfig();
        _host.Log.Info("设置", value ? "启动时最小化到托盘" : "启动时显示主窗口");
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (value is null || value.Value == Loc.Preference)
        {
            return;
        }

        Loc.Apply(value.Value);
        _host.Config.Language = value.Value.ToId();
        _host.SaveConfig();
        _host.Log.Info("设置", $"界面语言: {value.Value.ToId()}");
    }

    /// <summary>日志滚动到末尾的请求。</summary>
    public event Action? LogAdded;

    /// <summary>语言切换：所有由代码拼出来的文案都要重算。</summary>
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(UiFontFamily));
        UpdatePathTexts();
        RefreshLanguageOptions();

        var filter = SelectedLogLevel?.Value ?? LogLevelFilter.Info;
        RebuildLogLevelOptions(filter);

        // 盘符下拉里的「自动 / Auto」
        foreach (var option in DriveLetterOptions)
        {
            option.Refresh();
        }

        OnPropertyChanged(nameof(NewDrive));

        OnPropertyChanged(nameof(LogCountText));

        UpdateStatusChips();
        RefreshSessions();

        foreach (var item in Discovered)
        {
            item.Refresh();
        }
    }

    private void UpdatePathTexts()
    {
        OnPropertyChanged(nameof(AdbPathText));
        OnPropertyChanged(nameof(LogDirectoryText));
        OnPropertyChanged(nameof(ConfigDirectoryText));
    }

    private void BuildLanguageOptions()
    {
        foreach (var option in LanguageOption.CreateAll())
        {
            LanguageOptions.Add(option);
        }

        SelectedLanguage = LanguageOptions.FirstOrDefault(o => o.Value == Loc.Preference);
    }

    /// <summary>语言切换后只刷新「跟随系统」这类标签，集合本身不动。</summary>
    private void RefreshLanguageOptions()
    {
        foreach (var option in LanguageOptions)
        {
            option.Refresh();
        }

        OnPropertyChanged(nameof(SelectedLanguage));
    }

    private void RebuildLogLevelOptions(LogLevelFilter? keep)
    {
        var filter = keep ?? SelectedLogLevel?.Value ?? LogLevelFilter.Info;
        LogLevelOptions.Clear();
        foreach (var option in LogLevelOption.CreateAll())
        {
            LogLevelOptions.Add(option);
        }

        SelectedLogLevel = LogLevelOptions.FirstOrDefault(o => o.Value == filter);
    }

    private void UpdateStatusChips()
    {
        if (_host.AdbFound)
        {
            AdbStatus = Loc.T("status.adbReady");
            AdbStatusBrush = Brush.Parse("#4ADE80");
        }
        else
        {
            AdbStatus = Loc.T("status.adbMissing");
            AdbStatusBrush = Brush.Parse("#F87171");
        }

        DokanMissing = !_host.DokanInstalled;
        DokanStatus = _host.DokanInstalled ? Loc.T("status.dokanReady") : Loc.T("status.dokanMissing");
        DokanDetail = _host.DokanStatus.Text;
        DokanStatusBrush = _host.DokanInstalled ? Brush.Parse("#4ADE80") : Brush.Parse("#FBBF24");

        if (!_host.DokanInstalled)
        {
            BannerMessage = Loc.F("banner.dokanMissing", _host.DokanStatus.Text);
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

            DeviceCountText = Devices.Count == 0
                ? Loc.T("devices.countNone")
                : Loc.Count("devices.count", Devices.Count);
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

        OnPropertyChanged(nameof(LogCountText));
        LogAdded?.Invoke();
    }

    partial void OnSelectedLogLevelChanged(LogLevelOption? value) => RebuildLogs();

    private void RebuildLogs()
    {
        Logs.Clear();
        foreach (var entry in _logBuffer.Where(PassesFilter).TakeLast(MaxLogItems))
        {
            Logs.Add(new LogItemViewModel(entry));
        }

        OnPropertyChanged(nameof(LogCountText));
        LogAdded?.Invoke();
    }

    private bool PassesFilter(LogEntry entry) => SelectedLogLevel?.Value switch
    {
        LogLevelFilter.All => true,
        LogLevelFilter.Warnings => entry.Level >= LogLevel.Warn,
        LogLevelFilter.Errors => entry.Level >= LogLevel.Error,
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
                DiscoveredCountText = Loc.T("devices.serverUnavailable");
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
                    device.State,
                    device.IsOnline,
                    saved.Contains(device.Serial));
                item.MountCommand = new AsyncRelayCommand(() => MountDiscoveredAsync(item));
                Discovered.Add(item);
            }

            DiscoveredCountText = Discovered.Count == 0
                ? Loc.T("devices.discoveredNone")
                : Loc.Count("devices.discoveredCount", Discovered.Count);
        }
        catch (Exception ex)
        {
            DiscoveredCountText = Loc.T("devices.discoveredError");
            _host.Log.Warn("ADB", $"读取设备列表失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddDeviceAsync()
    {
        var endpoint = NormalizeEndpoint(NewEndpoint);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            BannerMessage = Loc.T("banner.needEndpoint");
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
            DriveLetter = NewDrive?.Letter?.ToString(),
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
                BannerMessage = Loc.T("banner.unauthorized");
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
        OnPropertyChanged(nameof(LogCountText));
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
    private void OpenConfigFolder()
    {
        var directory = Path.GetDirectoryName(_host.ConfigPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Log.Warn("设置", "打开配置目录失败: " + ex.Message);
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
        var status = DokanSupport.Check();
        DokanMissing = !status.Installed;
        DokanStatus = status.Installed ? Loc.T("status.dokanReady") : Loc.T("status.dokanMissing");
        DokanDetail = status.Text;
        DokanStatusBrush = status.Installed ? Brush.Parse("#4ADE80") : Brush.Parse("#FBBF24");
        if (status.Installed)
        {
            BannerVisible = false;
        }
        else
        {
            BannerMessage = Loc.F("banner.dokanMissing", status.Text);
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
