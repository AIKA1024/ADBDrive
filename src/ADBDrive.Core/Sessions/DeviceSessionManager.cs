using ADBDrive.Core.Adb;
using ADBDrive.Core.Config;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.Paths;

namespace ADBDrive.Core.Sessions;

/// <summary>设备会话集合：负责配置持久化与会话创建/销毁。</summary>
public sealed class DeviceSessionManager : IDisposable
{
    private readonly List<DeviceSession> _sessions = [];
    private bool _disposed;

    public DeviceSessionManager(AppLog log, AppConfig config, AdbServerClient server)
    {
        Log = log;
        Config = config;
        Server = server;
        Server.AdbExecutablePath ??= AdbLocator.Find(config.AdbPath);
    }

    public AppLog Log { get; }

    public AppConfig Config { get; }

    public AdbServerClient Server { get; }

    public IReadOnlyList<DeviceSession> Sessions => _sessions;

    public event Action? SessionsChanged;

    /// <summary>按配置创建设备会话（不自动连接）。</summary>
    public void LoadFromConfig()
    {
        foreach (var deviceConfig in Config.Devices)
        {
            _sessions.Add(CreateSession(deviceConfig));
        }

        SessionsChanged?.Invoke();
    }

    public DeviceSession AddDevice(DeviceConfig config)
    {
        var existing = _sessions.FirstOrDefault(s => string.Equals(s.Endpoint, config.Endpoint, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.UpdateConfig(config);
            return existing;
        }

        var session = CreateSession(config);
        _sessions.Add(session);
        Config.Devices.Add(config);
        SaveConfig();
        SessionsChanged?.Invoke();
        return session;
    }

    public void RemoveDevice(DeviceSession session)
    {
        session.Dispose();
        _sessions.Remove(session);
        Config.Devices.RemoveAll(d => string.Equals(d.Endpoint, session.Endpoint, StringComparison.OrdinalIgnoreCase));
        SaveConfig();
        SessionsChanged?.Invoke();
    }

    private DeviceSession CreateSession(DeviceConfig config)
    {
        var session = new DeviceSession(Server, config, Log);
        session.Changed += _ => SessionsChanged?.Invoke();
        return session;
    }

    /// <summary>启动时连接所有标记了自动连接的设备。</summary>
    public async Task ConnectAutoAsync()
    {
        foreach (var session in _sessions.Where(s => s.Config.AutoConnect).ToArray())
        {
            try
            {
                await session.ConnectAsync().ConfigureAwait(false);
                if (session.Config.AutoMount && session.State == SessionState.Connected)
                {
                    await session.MountAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("会话", $"自动连接 {session.Endpoint} 失败: {ex.Message}");
            }
        }
    }

    public void SaveConfig()
    {
        try
        {
            AppPaths.EnsureCreated();
            ConfigStore.Save(AppPaths.ConfigFile, Config);
        }
        catch (Exception ex)
        {
            Log.Warn("配置", $"保存配置失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var session in _sessions.ToArray())
        {
            session.Dispose();
        }

        _sessions.Clear();
        SaveConfig();
    }
}
