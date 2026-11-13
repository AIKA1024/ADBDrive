using ADBDrive.Core.Adb;
using ADBDrive.Core.Config;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.FileSystem;
using ADBDrive.Core.Mount;

namespace ADBDrive.Core.Sessions;

/// <summary>设备会话状态。</summary>
public enum SessionState
{
    Disconnected,
    Connecting,
    Connected,
    Mounted,
    Offline,
    Unauthorized,
    Error,
}

/// <summary>
/// 一台设备的完整生命周期：连接 -> 挂载 -> 健康检查 -> 断线重连。
/// 断线时不会卸载盘符（避免资源管理器里盘符忽然消失），
/// 而是让所有文件操作快速失败，并在后台按退避策略重连。
/// </summary>
public sealed class DeviceSession : IDisposable
{
    private readonly AdbServerClient _server;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _watchCts;
    private DeviceConfig _config;
    private SessionState _state = SessionState.Disconnected;
    private string _statusMessage = "未连接";
    private bool _everConnected;
    private bool _disposed;

    public DeviceSession(AdbServerClient server, DeviceConfig config, AppLog log)
    {
        _server = server;
        _config = config;
        _log = log;
    }

    public DeviceConfig Config => _config;

    public string Endpoint => _config.Endpoint;

    public SessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    public string StatusMessage
    {
        get
        {
            lock (_stateGate)
            {
                return _statusMessage;
            }
        }
    }

    public AdbDeviceInfo? Info { get; private set; }

    public AdbDeviceConnection? Device { get; private set; }

    public AdbFileSystem? FileSystem { get; private set; }

    public DokanMountSession? Mount { get; private set; }

    /// <summary>
    /// 设备 adbd 是否以 root 身份运行。为 true 时 sync 通道也是 root，
    /// 才能挂载 /、/data、/system 这类目录。
    /// </summary>
    public bool IsRoot { get; private set; }

    /// <summary>设备侧身份：root / shell / 未知。</summary>
    public string IdentityText { get; private set; } = "未知";

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_config.DisplayName))
            {
                return _config.DisplayName!;
            }

            return Info?.Model ?? Info?.Device ?? _config.Endpoint;
        }
    }

    /// <summary>状态变化（可能在后台线程触发）。</summary>
    public event Action<DeviceSession>? Changed;

    public void UpdateConfig(DeviceConfig config)
    {
        _config = config;
        if (FileSystem is not null)
        {
            FileSystem.CacheScale = config.UseCache ? 1 : 0;
        }

        RaiseChanged();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State is SessionState.Connected or SessionState.Mounted or SessionState.Connecting)
            {
                return;
            }

            SetState(SessionState.Connecting, "正在连接…");
            if (!await _server.EnsureServerAsync(ct).ConfigureAwait(false))
            {
                SetState(SessionState.Error, "adb server 不可用，请检查 adb.exe 路径");
                return;
            }

            if (IsTcpEndpoint)
            {
                var result = await _server.ConnectAsync(Endpoint, ct).ConfigureAwait(false);
                if (!result.Success)
                {
                    // connect 失败也继续看一眼 devices，可能是已经连上的会话。
                    _log.Warn("会话", $"connect {Endpoint}: {result.Message}");
                }
            }

            var info = await WaitForDeviceAsync(ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (info is null)
            {
                SetState(SessionState.Error, "设备未出现在 adb devices 中");
                StartWatchdog();
                return;
            }

            if (info.State == AdbDeviceState.Unauthorized)
            {
                SetState(SessionState.Unauthorized, "请在手机上确认「允许 USB 调试」授权");
                StartWatchdog();
                return;
            }

            if (!info.IsOnline)
            {
                SetState(SessionState.Offline, $"设备状态: {AdbServerClient.DescribeState(info.State)}");
                StartWatchdog();
                return;
            }

            Info = info;
            var device = new AdbDeviceConnection(_server, info, _log);
            await device.LoadFeaturesAsync(ct).ConfigureAwait(false);

            var mapper = new DevicePathMapper(_config.RootPath);
            var fileSystem = new AdbFileSystem(device, mapper, _log)
            {
                CacheScale = _config.UseCache ? 1 : 0,
            };

            await DetectIdentityAsync(device, ct).ConfigureAwait(false);

            var rootEntry = await fileSystem.ProbeRootAsync(ct).ConfigureAwait(false);
            if (rootEntry is null)
            {
                // 配置错误，不是临时故障，不启动看门狗反复重试。
                var hint = IsRoot
                    ? string.Empty
                    : "（当前 adbd 非 root，sync 通道只能访问 /sdcard、/storage/emulated/0 等共享目录）";
                SetState(SessionState.Error, $"根路径不存在或无法访问: {mapper.Root}{hint}");
                return;
            }

            Device = device;
            FileSystem = fileSystem;
            _everConnected = true;
            SetState(SessionState.Connected, $"已连接 · {DisplayName} · {mapper.Root}");
            StartWatchdog();
        }
        catch (OperationCanceledException)
        {
            SetState(SessionState.Error, "连接已取消");
        }
        catch (AdbException ex)
        {
            SetState(SessionState.Error, ex.Message);
            StartWatchdog();
        }
        catch (Exception ex)
        {
            _log.Error("会话", $"连接 {Endpoint} 失败", ex);
            SetState(SessionState.Error, ex.Message);
            StartWatchdog();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private bool IsTcpEndpoint => Endpoint.Contains(':');

    private async Task<AdbDeviceInfo?> WaitForDeviceAsync(CancellationToken ct, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var devices = await _server.ListDevicesAsync(ct).ConfigureAwait(false);
                var info = devices.FirstOrDefault(d => d.Serial == Endpoint);
                if (info is not null && (info.IsOnline || info.State == AdbDeviceState.Unauthorized))
                {
                    return info;
                }
            }
            catch (AdbException)
            {
                // adb server 可能刚起来，重试。
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>检测 adbd 身份。root 才能通过 sync 通道访问 /data、/system 等目录。</summary>
    private async Task DetectIdentityAsync(AdbDeviceConnection device, CancellationToken ct)
    {
        try
        {
            var uid = (await device.ExecuteAsync("id -u", ct).ConfigureAwait(false)).Trim();
            IsRoot = uid == "0";
            IdentityText = IsRoot ? "root" : (string.IsNullOrEmpty(uid) ? "未知" : $"uid {uid}");
            if (IsRoot)
            {
                _log.Info("会话", $"{Endpoint} 的 adbd 以 root 运行，可挂载全盘");
            }
        }
        catch (Exception ex)
        {
            IsRoot = false;
            IdentityText = "未知";
            _log.Debug("会话", $"检测设备身份失败: {ex.Message}");
        }
    }

    /// <summary>把用户输入的根路径规范化成以 / 开头、无结尾斜杠的形式。</summary>
    public static string NormalizeRootPath(string? raw)
    {
        var root = string.IsNullOrWhiteSpace(raw) ? "/sdcard" : raw.Trim().Replace('\\', '/');
        if (!root.StartsWith('/'))
        {
            root = "/" + root;
        }

        while (root.Length > 1 && root.EndsWith('/'))
        {
            root = root[..^1];
        }

        return root;
    }

    /// <summary>
    /// 切换挂载根路径。已连接时立即重建文件系统视图；已挂载时会自动「卸载 → 换根 → 重挂」，
    /// 保持同一个盘符字母。新根不可访问时不改动任何状态。
    /// </summary>
    public async Task<(bool Ok, string Message)> ChangeRootPathAsync(string rootPath, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var normalized = NormalizeRootPath(rootPath);
            if (string.Equals(normalized, _config.RootPath, StringComparison.Ordinal))
            {
                return (true, string.Empty);
            }

            var mapper = new DevicePathMapper(normalized);
            var connected = State is SessionState.Connected or SessionState.Mounted;
            if (!connected || Device is null)
            {
                _config.RootPath = normalized;
                RaiseChanged();
                return (true, $"根路径已改为 {normalized}，连接后生效");
            }

            var fileSystem = new AdbFileSystem(Device, mapper, _log)
            {
                CacheScale = _config.UseCache ? 1 : 0,
            };

            // 先验证新根真的可访问，避免把盘符切到一个打不开的地方。
            var probe = await fileSystem.ProbeRootAsync(ct).ConfigureAwait(false);
            if (probe is null)
            {
                var hint = IsRoot
                    ? "，该目录不存在或没有读取权限"
                    : "，该目录需要 root（adb root / Magisk 授权）才能访问";
                return (false, $"无法访问 {normalized}{hint}");
            }

            var wasMounted = State == SessionState.Mounted;
            if (wasMounted)
            {
                await UnmountAsync().ConfigureAwait(false);
            }

            var previousRoot = _config.RootPath;
            var previousFileSystem = FileSystem;
            _config.RootPath = normalized;
            FileSystem = fileSystem;

            if (!wasMounted)
            {
                SetState(SessionState.Connected, $"已连接 · {DisplayName} · {mapper.Root}");
                return (true, $"根路径已改为 {normalized}");
            }

            try
            {
                await MountCoreAsync(ct).ConfigureAwait(false);
                return (true, $"已切换到 {normalized}（{Mount?.DriveLetter}: 已重挂）");
            }
            catch (Exception ex)
            {
                // 重挂失败就回退到原来的根，尽量把盘符恢复回来。
                _config.RootPath = previousRoot;
                FileSystem = previousFileSystem;
                try
                {
                    await MountCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _log.Error("会话", "回退挂载失败", rollbackEx);
                }

                return (false, $"切换到 {normalized} 失败：{ex.Message}，已回退");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task MountAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MountCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>挂载主体。调用方必须已经持有 _lifecycle 锁。</summary>
    private async Task MountCoreAsync(CancellationToken ct)
    {
        {
            if (State == SessionState.Mounted)
            {
                return;
            }

            if (State != SessionState.Connected || FileSystem is null)
            {
                throw new InvalidOperationException("设备未连接，无法挂载");
            }

            var letter = ResolveDriveLetter();
            if (letter is null)
            {
                throw new InvalidOperationException("没有可用盘符");
            }

            var label = BuildVolumeLabel();
            var operations = new AdbDokanOperations(FileSystem, _log, label);
            var session = new DokanMountSession(letter.Value, _log);
            session.Mount(operations);
            Mount = session;
            ActiveLetters.Add(letter.Value);
            if (string.IsNullOrEmpty(_config.DriveLetter))
            {
                _config.DriveLetter = letter.Value.ToString();
            }

            SetState(SessionState.Mounted, $"{letter}: · {_config.RootPath}");
        }
    }

    public async Task UnmountAsync(TimeSpan? timeout = null)
    {
        var mount = Mount;
        Mount = null;
        if (mount is not null)
        {
            ActiveLetters.Remove(mount.DriveLetter);
            try
            {
                await mount.UnmountAsync(timeout ?? TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            }
            finally
            {
                mount.Dispose();
            }
        }

        if (State == SessionState.Mounted)
        {
            SetState(SessionState.Connected, "已卸载");
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnmountAsync().ConfigureAwait(false);
            StopWatchdog();
            FileSystem?.InvalidateAll();
            FileSystem = null;
            Device = null;
            if (IsTcpEndpoint)
            {
                await _server.DisconnectAsync(Endpoint, CancellationToken.None).ConfigureAwait(false);
            }

            SetState(SessionState.Disconnected, "未连接");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static readonly HashSet<char> ActiveLetters = [];

    private char? ResolveDriveLetter()
    {
        char preferred = '\0';
        if (!string.IsNullOrWhiteSpace(_config.DriveLetter) && _config.DriveLetter.Length > 0)
        {
            preferred = char.ToUpperInvariant(_config.DriveLetter[0]);
        }

        if (preferred != '\0' && !ActiveLetters.Contains(preferred) && DriveLetters.IsFree(preferred))
        {
            return preferred;
        }

        var used = DriveLetters.GetUsed();
        used.UnionWith(ActiveLetters);
        foreach (var letter in "ZYXWVUTSRQPONMLKJIHGFED")
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        return null;
    }

    private string BuildVolumeLabel()
    {
        var name = DisplayName;
        if (name.Length > 24)
        {
            name = name[..24];
        }

        return string.IsNullOrWhiteSpace(name) ? "ADBDrive" : name;
    }

    private void SetState(SessionState state, string message)
    {
        lock (_stateGate)
        {
            if (_state == state && _statusMessage == message)
            {
                return;
            }

            _state = state;
            _statusMessage = message;
        }

        _log.Info("会话", $"{Endpoint} => {state}: {message}");
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this);
        }
        catch (Exception ex)
        {
            _log.Debug("会话", $"状态回调异常: {ex.Message}");
        }
    }

    private void StartWatchdog()
    {
        if (_watchCts is not null || _disposed)
        {
            return;
        }

        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        _ = Task.Run(() => WatchLoopAsync(token), CancellationToken.None);
    }

    private void StopWatchdog()
    {
        try
        {
            _watchCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _watchCts?.Dispose();
        _watchCts = null;
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var state = State;
                if (state is SessionState.Connected or SessionState.Mounted)
                {
                    if (!await ProbeAsync(ct).ConfigureAwait(false))
                    {
                        FileSystem?.InvalidateAll();
                        SetState(SessionState.Offline, "设备已离线，正在尝试重连…");
                    }
                    else
                    {
                        backoff = TimeSpan.FromSeconds(2);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                    continue;
                }

                if (state == SessionState.Disconnected || !_everConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }

                // 离线 / 未授权 / 错误：按退避重试连接。
                var recovered = await ReconnectAsync(ct).ConfigureAwait(false);
                if (recovered)
                {
                    backoff = TimeSpan.FromSeconds(2);
                    continue;
                }

                await Task.Delay(backoff, ct).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 1.5));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Debug("会话", $"看门狗异常: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        var devices = await _server.ListDevicesAsync(ct).ConfigureAwait(false);
        var info = devices.FirstOrDefault(d => d.Serial == Endpoint);
        if (info is null)
        {
            _log.Warn("会话", $"{Endpoint} 已从 adb devices 中消失");
            return false;
        }

        Info = info;
        Device?.RefreshInfo(info);
        if (info.State == AdbDeviceState.Unauthorized)
        {
            SetState(SessionState.Unauthorized, "授权被撤销，请在手机上重新确认");
            return false;
        }

        if (!info.IsOnline)
        {
            return false;
        }

        if (State == SessionState.Mounted)
        {
            SetState(SessionState.Mounted, $"{Mount?.DriveLetter}: · {_config.RootPath}");
        }
        else
        {
            SetState(SessionState.Connected, $"已连接 · {DisplayName} · {_config.RootPath}");
        }

        return true;
    }

    private async Task<bool> ReconnectAsync(CancellationToken ct)
    {
        if (!await _server.EnsureServerAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        if (IsTcpEndpoint)
        {
            var result = await _server.ConnectAsync(Endpoint, ct).ConfigureAwait(false);
            _log.Info("会话", $"重连 {Endpoint}: {result.Message}");
        }

        var info = await WaitForDeviceAsync(ct, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        if (info is null)
        {
            SetState(SessionState.Offline, "设备离线，重试中…");
            return false;
        }

        if (info.State == AdbDeviceState.Unauthorized)
        {
            SetState(SessionState.Unauthorized, "请在手机上确认授权");
            return false;
        }

        if (!info.IsOnline)
        {
            SetState(SessionState.Offline, "设备离线，重试中…");
            return false;
        }

        Info = info;
        if (Device is null)
        {
            var device = new AdbDeviceConnection(_server, info, _log);
            await device.LoadFeaturesAsync(ct).ConfigureAwait(false);
            Device = device;
            FileSystem = new AdbFileSystem(device, new DevicePathMapper(_config.RootPath), _log)
            {
                CacheScale = _config.UseCache ? 1 : 0,
            };
        }
        else
        {
            Device.RefreshInfo(info);
            FileSystem?.InvalidateAll();
        }

        var mounted = Mount is not null;
        SetState(mounted ? SessionState.Mounted : SessionState.Connected,
            mounted ? $"已恢复连接 · {Mount!.DriveLetter}:" : $"已重连 · {DisplayName}");
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatchdog();
        try
        {
            var mount = Mount;
            Mount = null;
            if (mount is not null)
            {
                ActiveLetters.Remove(mount.DriveLetter);
                mount.UnmountAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                mount.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        _lifecycle.Dispose();
    }
}
