using ADBDrive.Core.Diagnostics;

namespace ADBDrive.Core.Adb;

/// <summary>
/// 一个已连通的 ADB 设备通道：按需开 transport，屏蔽 v1/v2 差异。
/// 每次操作独立开连接，避免并发操作互相干扰（本机 socket 建连几乎无成本）。
/// </summary>
public sealed class AdbDeviceConnection
{
    private readonly AdbServerClient _server;
    private readonly AppLog _log;

    public AdbDeviceConnection(AdbServerClient server, AdbDeviceInfo info, AppLog log)
    {
        _server = server;
        _log = log;
        Info = info;
        Serial = info.Serial;
    }

    public AdbDeviceInfo Info { get; private set; }

    public string Serial { get; }

    public bool SupportsSyncV2 { get; private set; }

    public bool SupportsShellV2 { get; private set; }

    public string DisplayName => Info.DisplayName;

    /// <summary>读取设备 feature 列表，决定使用 sync v2 / shell v2。</summary>
    public async Task LoadFeaturesAsync(CancellationToken ct)
    {
        var features = await _server.GetFeaturesAsync(Serial, ct).ConfigureAwait(false);
        SupportsSyncV2 = features.Contains("stat_v2") && features.Contains("ls_v2");
        SupportsShellV2 = features.Contains("shell_v2");
        _log.Debug("ADB", $"设备 {Serial} features: sync_v2={SupportsSyncV2}, shell_v2={SupportsShellV2}");
    }

    public void RefreshInfo(AdbDeviceInfo info) => Info = info;

    /// <summary>打开一条 sync 连接。调用方负责释放。</summary>
    public async Task<AdbSyncConnection> OpenSyncAsync(CancellationToken ct)
    {
        var transport = await _server.OpenTransportAsync(Serial, ct).ConfigureAwait(false);
        try
        {
            return await transport.OpenSyncAsync(SupportsSyncV2, ct).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>执行一条 shell 命令。</summary>
    public async Task<AdbShellResult> ShellAsync(string command, CancellationToken ct)
    {
        try
        {
            var transport = await _server.OpenTransportAsync(Serial, ct).ConfigureAwait(false);
            try
            {
                return await transport.ShellAsync(command, SupportsShellV2, ct).ConfigureAwait(false);
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (AdbException ex) when (ex.Kind == AdbErrorKind.ProtocolError && SupportsShellV2)
        {
            // 少数设备会谎报 shell_v2，回退一次。
            SupportsShellV2 = false;
            _log.Warn("ADB", $"设备 {Serial} shell v2 不可用，回退到传统 shell");
            var transport = await _server.OpenTransportAsync(Serial, ct).ConfigureAwait(false);
            try
            {
                return await transport.ShellAsync(command, false, ct).ConfigureAwait(false);
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>执行命令并要求成功，失败时抛 AdbException（携带 stderr）。</summary>
    public async Task<string> ExecuteAsync(string command, CancellationToken ct)
    {
        var result = await ShellAsync(command, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw AdbException.FromDeviceMessage(message.Trim(), $"命令失败: {command}");
        }

        return result.StandardOutput;
    }
}
