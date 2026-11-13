using System.Text;
using ADBDrive.Core.Adb;

namespace ADBDrive.Core.Adb;

/// <summary>
/// 一条已切到指定设备的 ADB transport。每个 transport 只能承载一个服务（sync / shell / ...），
/// 因此并发操作需要各自开 transport——本机 socket 建连成本极低。
/// </summary>
public sealed class AdbTransport : IAsyncDisposable
{
    private readonly AdbServerClient.AdbSocket _socket;
    private bool _serviceOpened;

    internal AdbTransport(string serial, AdbServerClient.AdbSocket socket)
    {
        Serial = serial;
        _socket = socket;
    }

    public string Serial { get; }

    public Stream Stream => _socket.Stream;

    /// <summary>在 transport 上打开一个服务（如 "sync:" / "shell,v2,raw:ls"）。</summary>
    public async Task<Stream> OpenServiceAsync(string service, CancellationToken ct)
    {
        if (_serviceOpened)
        {
            throw new InvalidOperationException("该 transport 已经承载了一个服务，请另开一条 transport。");
        }

        await AdbWire.SendRequestCheckedAsync(_socket.Stream, service, ct).ConfigureAwait(false);
        _serviceOpened = true;
        return _socket.Stream;
    }

    /// <summary>打开 sync 服务。</summary>
    public async Task<AdbSyncConnection> OpenSyncAsync(bool useV2, CancellationToken ct)
    {
        var stream = await OpenServiceAsync("sync:", ct).ConfigureAwait(false);
        return new AdbSyncConnection(stream, useV2);
    }

    /// <summary>执行 shell 命令。</summary>
    public async Task<AdbShellResult> ShellAsync(string command, bool useShellV2, CancellationToken ct)
    {
        var service = useShellV2 ? "shell,v2,raw:" + command : "shell:" + command;
        var stream = await OpenServiceAsync(service, ct).ConfigureAwait(false);
        return await AdbShellProtocol.RunAsync(stream, useShellV2, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _socket.DisposeAsync();
}

/// <summary>SHELL v2 协议（带 stdout/stderr/退出码分离）与非 v2 的兼容实现。</summary>
internal static class AdbShellProtocol
{
    public static async Task<AdbShellResult> RunAsync(Stream stream, bool useV2, CancellationToken ct)
    {
        if (!useV2)
        {
            var text = await ReadToEndAsync(stream, ct).ConfigureAwait(false);
            return new AdbShellResult(0, text, string.Empty);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = 0;
        var header = new byte[5];

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(header.AsMemory(0, 5), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            if (read < 5)
            {
                // 连接在包中途断开，尽力而为。
                await ReadExactlyBestEffortAsync(stream, header.AsMemory(read, 5 - read), ct).ConfigureAwait(false);
            }

            var id = header[0];
            var length = AdbWire.ReadInt32LittleEndian(header.AsSpan(1, 4));
            if (length < 0 || length > 1 << 20)
            {
                break;
            }

            byte[]? payload = null;
            if (length > 0)
            {
                payload = new byte[length];
                await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            }

            switch (id)
            {
                case 1: // stdout
                    if (payload is not null)
                    {
                        stdout.Append(Encoding.UTF8.GetString(payload));
                    }

                    break;
                case 2: // stderr
                    if (payload is not null)
                    {
                        stderr.Append(Encoding.UTF8.GetString(payload));
                    }

                    break;
                case 3: // exit
                    if (payload is { Length: > 0 })
                    {
                        exitCode = payload[0];
                    }

                    return new AdbShellResult(exitCode, stdout.ToString(), stderr.ToString());
                case 4: // close stdin
                default:
                    break;
            }
        }

        return new AdbShellResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task ReadExactlyBestEffortAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }
}
