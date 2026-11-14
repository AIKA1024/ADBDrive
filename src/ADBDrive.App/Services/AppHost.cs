using ADBDrive.Core.Adb;
using ADBDrive.Core.Config;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.Localization;
using ADBDrive.Core.Mount;
using ADBDrive.Core.Paths;
using ADBDrive.Core.Sessions;

namespace ADBDrive.Services;

/// <summary>应用的组合根：日志、配置、ADB server 客户端、会话管理器。</summary>
public sealed class AppHost : IDisposable
{
    private bool _disposed;

    public AppHost()
    {
        AppPaths.EnsureCreated();
        Log = new AppLog(AppPaths.LogDirectory);
        Config = ConfigStore.Load(AppPaths.ConfigFile);

        // 语言要在创建任何界面之前生效，否则首帧会用默认语言渲染。
        Loc.Apply(Config.Language);

        AdbPath = AdbLocator.Find(Config.AdbPath);
        Server = new AdbServerClient(Log, AdbPath);
        Manager = new DeviceSessionManager(Log, Config, Server);
        DokanStatus = DokanSupport.Check();
        DokanInstalled = DokanStatus.Installed;

        Log.Info("启动", $"ADBDrive 启动，adb={AdbPath ?? "<未找到>"}，{DokanStatus.Text}");
        Manager.LoadFromConfig();
    }

    public AppLog Log { get; }

    public AppConfig Config { get; }

    public AdbServerClient Server { get; }

    public DeviceSessionManager Manager { get; }

    public string? AdbPath { get; }

    public bool AdbFound => !string.IsNullOrWhiteSpace(AdbPath);

    public bool DokanInstalled { get; }

    public DokanStatusInfo DokanStatus { get; }

    public string ConfigPath => AppPaths.ConfigFile;

    /// <summary>启动后的后台初始化：确保 adb server 可用并拉一次设备列表。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (!await Server.EnsureServerAsync(CancellationToken.None).ConfigureAwait(false))
            {
                Log.Warn("启动", "adb server 不可用");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("启动", $"初始化 adb server 失败: {ex.Message}");
        }

        try
        {
            await Manager.ConnectAutoAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("启动", $"自动连接失败: {ex.Message}");
        }
    }

    public void SaveConfig() => Manager.SaveConfig();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Manager.Dispose();
        Log.Info("退出", "ADBDrive 已退出");
        Log.Dispose();
    }
}
