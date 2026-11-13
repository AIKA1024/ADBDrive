using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using ADBDrive.Core.Diagnostics;

namespace ADBDrive.Core.Adb;

/// <summary>
/// 与本机 ADB server（默认 127.0.0.1:5037）通信的客户端。
/// 直接走 socket 协议而不是每条命令都起 adb.exe 进程，避免进程创建开销。
/// </summary>
public sealed class AdbServerClient
{
    public const int DefaultServerPort = 5037;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly AppLog _log;
    private readonly string _serverHost;
    private readonly int _serverPort;

    public AdbServerClient(AppLog log, string? adbExecutablePath = null, string serverHost = "127.0.0.1", int serverPort = DefaultServerPort)
    {
        _log = log;
        _serverHost = serverHost;
        _serverPort = serverPort;
        AdbExecutablePath = adbExecutablePath;
    }

    /// <summary>adb.exe 路径；仅用于启动 server 和 connect/disconnect 兜底。</summary>
    public string? AdbExecutablePath { get; set; }

    public string Endpoint => $"{_serverHost}:{_serverPort}";

    /// <summary>探测 server 是否在监听。不做重试。</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var client = await ConnectSocketAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>确保 adb server 在运行；必要时拉起 adb.exe start-server。</summary>
    public async Task<bool> EnsureServerAsync(CancellationToken ct)
    {
        if (await PingAsync(ct).ConfigureAwait(false))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(AdbExecutablePath) || !File.Exists(AdbExecutablePath))
        {
            _log.Error("ADB", $"adb server 未运行，且找不到 adb.exe（路径: {AdbExecutablePath ?? "<空>"}）");
            return false;
        }

        _log.Info("ADB", $"启动 adb server: {AdbExecutablePath} start-server");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(AdbExecutablePath, "start-server")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
            {
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }
            }

            // 端口就绪需要一点时间，最多等 5 秒。
            for (var i = 0; i < 25; i++)
            {
                if (await PingAsync(ct).ConfigureAwait(false))
                {
                    _log.Info("ADB", "adb server 已就绪");
                    return true;
                }

                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("ADB", "启动 adb server 失败", ex);
        }

        return false;
    }

    public async Task<string> GetServerVersionAsync(CancellationToken ct)
    {
        await using var socket = await OpenHostConnectionAsync(ct).ConfigureAwait(false);
        await AdbWire.SendRequestCheckedAsync(socket.Stream, "host:version", ct).ConfigureAwait(false);
        return await AdbWire.ReadMessageAsync(socket.Stream, ct).ConfigureAwait(false);
    }

    /// <summary>读取 adb devices -l 列表。</summary>
    public async Task<List<AdbDeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        await using var socket = await OpenHostConnectionAsync(ct).ConfigureAwait(false);
        await AdbWire.SendRequestCheckedAsync(socket.Stream, "host:devices-l", ct).ConfigureAwait(false);
        var text = await AdbWire.ReadMessageAsync(socket.Stream, ct).ConfigureAwait(false);
        return ParseDevices(text);
    }

    /// <summary>读取指定设备的 feature 列表（用于判断 stat_v2 / ls_v2 是否可用）。</summary>
    public async Task<HashSet<string>> GetFeaturesAsync(string serial, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var socket = await OpenHostConnectionAsync(ct).ConfigureAwait(false);
            await AdbWire.SendRequestCheckedAsync(socket.Stream, $"host-serial:{serial}:features", ct).ConfigureAwait(false);
            var text = await AdbWire.ReadMessageAsync(socket.Stream, ct).ConfigureAwait(false);
            foreach (var feature in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.Add(feature);
            }
        }
        catch (Exception ex)
        {
            _log.Debug("ADB", $"读取设备 {serial} features 失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>adb connect host:port。返回设备端返回的原始消息。</summary>
    public async Task<AdbConnectResult> ConnectAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            await using var socket = await OpenHostConnectionAsync(ct).ConfigureAwait(false);
            await AdbWire.SendRequestCheckedAsync(socket.Stream, $"host:connect:{endpoint}", ct).ConfigureAwait(false);
            var message = await AdbWire.ReadMessageAsync(socket.Stream, ct).ConfigureAwait(false);
            var success = message.Contains("connected to", StringComparison.OrdinalIgnoreCase)
                          && !message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                          && !message.Contains("cannot", StringComparison.OrdinalIgnoreCase);
            _log.Info("ADB", $"connect {endpoint} => {message}");
            return new AdbConnectResult(success, endpoint, message);
        }
        catch (AdbException ex)
        {
            _log.Warn("ADB", $"connect {endpoint} 失败: {ex.Message}");
            return new AdbConnectResult(false, endpoint, ex.Message);
        }
    }

    public async Task DisconnectAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            await using var socket = await OpenHostConnectionAsync(ct).ConfigureAwait(false);
            await AdbWire.SendRequestCheckedAsync(socket.Stream, $"host:disconnect:{endpoint}", ct).ConfigureAwait(false);
            var message = await AdbWire.ReadMessageAsync(socket.Stream, ct).ConfigureAwait(false);
            _log.Info("ADB", $"disconnect {endpoint} => {message}");
        }
        catch (Exception ex)
        {
            _log.Debug("ADB", $"disconnect {endpoint} 异常: {ex.Message}");
        }
    }

    /// <summary>打开一条 transport（等价于把 socket 切到某个设备）。</summary>
    public async Task<AdbTransport> OpenTransportAsync(string serial, CancellationToken ct)
    {
        var socket = await OpenHostConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await AdbWire.SendRequestCheckedAsync(socket.Stream, $"host:transport:{serial}", ct).ConfigureAwait(false);
            return new AdbTransport(serial, socket);
        }
        catch
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AdbSocket> OpenHostConnectionAsync(CancellationToken ct)
    {
        var client = await ConnectSocketAsync(ct).ConfigureAwait(false);
        return new AdbSocket(client);
    }

    private async Task<TcpClient> ConnectSocketAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(_serverHost, _serverPort, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new AdbException(AdbErrorKind.ServerUnavailable, $"连接 adb server {Endpoint} 超时");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new AdbException(AdbErrorKind.ServerUnavailable, $"连接 adb server {Endpoint} 失败: {ex.Message}", ex);
        }
    }

    /// <summary>解析 adb devices -l 的输出。</summary>
    public static List<AdbDeviceInfo> ParseDevices(string text)
    {
        var devices = new List<AdbDeviceInfo>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return devices;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith('*'))
            {
                continue;
            }

            // 格式: <serial>\t<state> [key:value ...]
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var serial = parts[0];
            var state = AdbDeviceInfo.ParseState(parts[1]);
            string? product = null, model = null, device = null;
            int? transportId = null;

            for (var i = 2; i < parts.Length; i++)
            {
                var token = parts[i];
                var colon = token.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = token[..colon];
                var value = token[(colon + 1)..];
                switch (key)
                {
                    case "product":
                        product = value;
                        break;
                    case "model":
                        model = value;
                        break;
                    case "device":
                        device = value;
                        break;
                    case "transport_id" when int.TryParse(value, out var id):
                        transportId = id;
                        break;
                }
            }

            devices.Add(new AdbDeviceInfo(serial, state, product, model, device, transportId));
        }

        return devices;
    }

    /// <summary>把一个 TcpClient 包装成带缓冲的读写器。</summary>
    public sealed class AdbSocket : IAsyncDisposable
    {
        private readonly TcpClient _client;

        internal AdbSocket(TcpClient client)
        {
            _client = client;
            Stream = client.GetStream();
        }

        public NetworkStream Stream { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                Stream.Dispose();
                _client.Dispose();
            }
            catch
            {
                // ignore
            }

            return ValueTask.CompletedTask;
        }
    }

    public static string DescribeState(AdbDeviceState state) => state switch
    {
        AdbDeviceState.Device => "在线",
        AdbDeviceState.Offline => "离线",
        AdbDeviceState.Unauthorized => "未授权",
        AdbDeviceState.Recovery => "Recovery",
        AdbDeviceState.Bootloader => "Bootloader",
        AdbDeviceState.Sideload => "Sideload",
        AdbDeviceState.NoPermissions => "无权限",
        _ => "未知",
    };

    internal static string Escape(string value) => value.Replace("\r", " ").Replace("\n", " ");
}
