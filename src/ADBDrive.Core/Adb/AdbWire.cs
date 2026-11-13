using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ADBDrive.Core.Adb;

/// <summary>
/// ADB 线路协议的底层编解码。
/// - host/smart-socket 层：4 位十六进制长度 + ASCII 请求，应答 OKAY / FAIL + 4 位十六进制长度 + 文本
/// - sync 层：4 字节 ASCII 标识 + 4 字节小端长度 + 负载
/// </summary>
internal static class AdbWire
{
    public const string Okay = "OKAY";
    public const string Fail = "FAIL";

    public static byte[] BuildRequest(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var buffer = new byte[4 + payload.Length];
        WriteHexLength(buffer.AsSpan(0, 4), payload.Length);
        payload.CopyTo(buffer, 4);
        return buffer;
    }

    public static void WriteHexLength(Span<byte> destination, int length)
    {
        if (!length.TryFormat(destination, out _, "x4"))
        {
            // 长度超过 0xFFFF 时 ADB 协议本身就无法表达，这里属于编程错误。
            throw new AdbException(AdbErrorKind.ProtocolError, $"请求长度过大: {length}");
        }
    }

    public static async Task WriteRequestAsync(Stream stream, string text, CancellationToken ct)
    {
        var buffer = BuildRequest(text);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>读取 4 字节状态字（OKAY / FAIL）。连接被关闭时抛 Offline。</summary>
    public static async Task<string> ReadStatusAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return Encoding.ASCII.GetString(buffer);
    }

    /// <summary>读取 "4 位十六进制长度 + 文本" 的消息体。</summary>
    public static async Task<string> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = ParseHexLength(header);
        if (length == 0)
        {
            return string.Empty;
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    public static int ParseHexLength(ReadOnlySpan<byte> header)
    {
        var text = Encoding.ASCII.GetString(header);
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>发送请求并校验应答；FAIL 时抛出带设备消息的异常。</summary>
    public static async Task SendRequestCheckedAsync(Stream stream, string request, CancellationToken ct)
    {
        await WriteRequestAsync(stream, request, ct).ConfigureAwait(false);
        var status = await ReadStatusAsync(stream, ct).ConfigureAwait(false);
        if (status == Okay)
        {
            return;
        }

        var message = status == Fail ? await ReadMessageAsync(stream, ct).ConfigureAwait(false) : status;
        throw MapServiceFailure(request, message);
    }

    public static AdbException MapServiceFailure(string request, string message)
    {
        var kind = AdbException.ClassifyDeviceMessage(message);
        // 没有明确分类时，transport 阶段失败基本等价于设备不在线。
        if (kind == AdbErrorKind.Unknown)
        {
            if (message.Contains("offline", StringComparison.OrdinalIgnoreCase))
            {
                kind = AdbErrorKind.Offline;
            }
            else if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("no devices", StringComparison.OrdinalIgnoreCase))
            {
                kind = AdbErrorKind.DeviceNotFound;
            }
        }

        return new AdbException(kind, message.Length == 0 ? $"ADB 请求失败: {request}" : message);
    }

    public static int ReadInt32LittleEndian(ReadOnlySpan<byte> buffer) =>
        BinaryPrimitives.ReadInt32LittleEndian(buffer);

    public static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> buffer) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer);

    public static void WriteInt32LittleEndian(Span<byte> buffer, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
}
