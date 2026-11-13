using System.Buffers.Binary;
using System.Text;

namespace ADBDrive.Core.Adb;

/// <summary>
/// ADB sync 协议的客户端实现（目录枚举、属性、流式收发）。
/// 结构与取值均按 adb 实测字节序实现：
/// <list type="bullet">
/// <item>v2 stat: id("LST2") + 68 字节 struct（error/dev/ino/mode/nlink/uid/gid/size/mtime/atime/ctime）</item>
/// <item>v2 目录项: id("DNT2") + 68 字节 struct + namelen + name</item>
/// <item>v1 stat: id("STAT") + mode + size + time（共 16 字节）</item>
/// <item>v1 目录项: id("DENT") + mode + size + time + namelen + name</item>
/// </list>
/// </summary>
public sealed class AdbSyncConnection : IAsyncDisposable
{
    private const int V2StatSize = 68;
    private const int V1StatSize = 12;
    private const int MaxNameLength = 4096;

    private readonly Stream _stream;
    private readonly bool _useV2;
    private bool _disposed;

    internal AdbSyncConnection(Stream stream, bool useV2)
    {
        _stream = stream;
        _useV2 = useV2;
    }

    public bool SupportsV2 => _useV2;

    /// <summary>查询路径属性。路径不存在时返回 null（权限不足则抛异常）。</summary>
    public async Task<AdbEntry?> StatAsync(string path, CancellationToken ct)
    {
        var payload = EncodePath(path);
        if (_useV2)
        {
            await SendRequestAsync("LST2", payload, ct).ConfigureAwait(false);
            var header = new byte[4 + V2StatSize];
            await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var id = Encoding.ASCII.GetString(header, 0, 4);
            if (id == AdbWire.Fail)
            {
                throw AdbException.FromDeviceMessage(await ReadFailureMessageAsync(ct).ConfigureAwait(false));
            }

            if (id != "LST2")
            {
                throw new AdbException(AdbErrorKind.ProtocolError, $"LST2 应答异常: {id}");
            }

            return ParseStatV2(header.AsSpan(4), path);
        }

        await SendRequestAsync("STAT", payload, ct).ConfigureAwait(false);
        var legacy = new byte[4 + V1StatSize];
        await _stream.ReadExactlyAsync(legacy, ct).ConfigureAwait(false);
        var legacyId = Encoding.ASCII.GetString(legacy, 0, 4);
        if (legacyId != "STAT")
        {
            throw new AdbException(AdbErrorKind.ProtocolError, $"STAT 应答异常: {legacyId}");
        }

        return ParseStatV1(legacy.AsSpan(4), path);
    }

    /// <summary>枚举目录。目录不存在 / 无权限时抛异常。</summary>
    public async Task<List<AdbEntry>> ListAsync(string path, CancellationToken ct)
    {
        var entries = new List<AdbEntry>();
        await SendRequestAsync(_useV2 ? "LIS2" : "LIST", EncodePath(path), ct).ConfigureAwait(false);

        // 注意：v2 的条目结构是 id + 68 字节 stat（其中前 4 字节是 error），
        // 不存在通用的 "id + 长度" 头；因此必须先读 4 字节 id，再按版本分别解析。
        var idBuffer = new byte[4];
        var lengthBuffer = new byte[4];

        while (true)
        {
            await _stream.ReadExactlyAsync(idBuffer, ct).ConfigureAwait(false);
            var id = Encoding.ASCII.GetString(idBuffer, 0, 4);

            if (id == "DONE")
            {
                await _stream.ReadExactlyAsync(lengthBuffer, ct).ConfigureAwait(false);
                break;
            }

            if (id == AdbWire.Fail)
            {
                await _stream.ReadExactlyAsync(lengthBuffer, ct).ConfigureAwait(false);
                var length = AdbWire.ReadInt32LittleEndian(lengthBuffer);
                var message = await ReadTextAsync(length, ct).ConfigureAwait(false);
                throw AdbException.FromDeviceMessage(message, $"列出目录 {path} 失败");
            }

            if (_useV2)
            {
                if (id != "DNT2")
                {
                    throw new AdbException(AdbErrorKind.ProtocolError, $"LIS2 应答异常: {id}");
                }

                var stat = new byte[V2StatSize];
                await _stream.ReadExactlyAsync(stat, ct).ConfigureAwait(false);
                await _stream.ReadExactlyAsync(lengthBuffer, ct).ConfigureAwait(false);
                var nameLength = AdbWire.ReadInt32LittleEndian(lengthBuffer);
                var name = await ReadTextAsync(nameLength, ct).ConfigureAwait(false);
                AppendEntry(entries, stat, name, path);
            }
            else
            {
                if (id != "DENT")
                {
                    throw new AdbException(AdbErrorKind.ProtocolError, $"LIST 应答异常: {id}");
                }

                var stat = new byte[V1StatSize];
                await _stream.ReadExactlyAsync(stat, ct).ConfigureAwait(false);
                await _stream.ReadExactlyAsync(lengthBuffer, ct).ConfigureAwait(false);
                var nameLength = AdbWire.ReadInt32LittleEndian(lengthBuffer);
                var name = await ReadTextAsync(nameLength, ct).ConfigureAwait(false);
                AppendEntryV1(entries, stat, name, path);
            }
        }

        return entries;
    }

    /// <summary>打开一个只读数据流（RECV）。返回的流被释放时会一并关闭本连接。</summary>
    public async Task<Stream> OpenReadAsync(string path, long sizeHint, CancellationToken ct)
    {
        await SendRequestAsync("RECV", EncodePath(path), ct).ConfigureAwait(false);
        return new AdbRecvStream(_stream, this, sizeHint);
    }

    /// <summary>把本地数据流推送为远端文件（SEND）。</summary>
    public async Task PushAsync(string remotePath, Stream source, DateTimeOffset? modified, CancellationToken ct)
    {
        await SendRequestAsync("SEND", EncodePathWithMode(remotePath, mode: 420), ct).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await WriteFrameHeaderAsync("DATA", read, ct).ConfigureAwait(false);
            await _stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        // 结束包：DONE + 4 字节修改时间（0 会被设备当成 1970，必须给真实时间）。
        var done = new byte[8];
        Encoding.ASCII.GetBytes("DONE").CopyTo(done, 0);
        var seconds = (uint)Math.Clamp((modified ?? DateTimeOffset.Now).ToUnixTimeSeconds(), 1, uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(4), seconds);
        await _stream.WriteAsync(done, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[8];
        await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var id = Encoding.ASCII.GetString(header, 0, 4);
        if (id == AdbWire.Fail)
        {
            var length = AdbWire.ReadInt32LittleEndian(header.AsSpan(4));
            var message = await ReadTextAsync(length, ct).ConfigureAwait(false);
            throw AdbException.FromDeviceMessage(message, $"写入 {remotePath} 失败");
        }
    }

    /// <summary>发送 QUIT，让设备端优雅结束 sync 会话。</summary>
    public async Task QuitAsync(CancellationToken ct)
    {
        try
        {
            await SendRequestAsync("QUIT", Array.Empty<byte>(), ct).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private void AppendEntry(List<AdbEntry> entries, byte[] stat, string name, string parent)
    {
        var error = BinaryPrimitives.ReadUInt32LittleEndian(stat);
        if (error != 0)
        {
            return;
        }

        var mode = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(20));
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(36));
        // 注意字段顺序：size 之后是 atime、mtime、ctime（实测 adbd 就是按这个顺序序列化的）。
        var mtime = (long)BinaryPrimitives.ReadUInt64LittleEndian(stat.AsSpan(52));
        entries.Add(CreateEntry(name, parent, mode, size, mtime));
    }

    private void AppendEntryV1(List<AdbEntry> entries, byte[] stat, string name, string parent)
    {
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(stat);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(4));
        var mtime = BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(8));
        entries.Add(CreateEntry(name, parent, mode, size, mtime));
    }

    private static AdbEntry? ParseStatV2(ReadOnlySpan<byte> stat, string path)
    {
        var error = BinaryPrimitives.ReadUInt32LittleEndian(stat);
        if (error != 0)
        {
            if (error == 2 || error == 20)
            {
                return null;
            }

            throw new AdbException(AdbErrno.ToKind((int)error), $"查询 {path} 失败: {AdbErrno.ToMessage((int)error)}");
        }

        var mode = BinaryPrimitives.ReadUInt32LittleEndian(stat[20..]);
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(stat[36..]);
        var mtime = (long)BinaryPrimitives.ReadUInt64LittleEndian(stat[52..]);
        var name = GetLeafName(path);
        return CreateEntry(name, GetParentPath(path), mode, size, mtime);
    }

    private static AdbEntry? ParseStatV1(ReadOnlySpan<byte> stat, string path)
    {
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(stat);
        if (mode == 0)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(stat[4..]);
        var mtime = BinaryPrimitives.ReadUInt32LittleEndian(stat[8..]);
        return CreateEntry(GetLeafName(path), GetParentPath(path), mode, size, mtime);
    }

    private static AdbEntry CreateEntry(string name, string parent, uint mode, long size, long mtime)
    {
        const uint typeMask = 0xF000;
        const uint typeDir = 0x4000;
        const uint typeLnk = 0xA000;
        var type = mode & typeMask;
        var modified = mtime > 0 && mtime < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(mtime)
            : DateTimeOffset.UnixEpoch;
        var fullPath = parent.EndsWith('/') ? parent + name : parent + "/" + name;
        return new AdbEntry(name, fullPath, type == typeDir, type == typeLnk, size, modified, mode);
    }

    internal static string GetLeafName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "/";
        }

        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    internal static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "/";
        }

        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private async Task SendRequestAsync(string id, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(frame, 0);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteFrameHeaderAsync(string id, int length, CancellationToken ct)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(id).CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length);
        await _stream.WriteAsync(header, ct).ConfigureAwait(false);
    }

    private static byte[] EncodePath(string path) => Encoding.UTF8.GetBytes(path + "\0");

    private static byte[] EncodePathWithMode(string path, int mode)
    {
        // v1 SEND 的负载是 "path,mode\0"（mode 为十进制，例如 420 = 0644）。
        return Encoding.UTF8.GetBytes(path + "," + mode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
    }

    private async Task<string> ReadTextAsync(int length, CancellationToken ct)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        if (length > MaxNameLength * 16)
        {
            throw new AdbException(AdbErrorKind.ProtocolError, $"sync 报文长度异常: {length}");
        }

        var buffer = new byte[length];
        await _stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer);
    }

    private async Task<string> ReadFailureMessageAsync(CancellationToken ct)
    {
        var lengthBuffer = new byte[4];
        await _stream.ReadExactlyAsync(lengthBuffer, ct).ConfigureAwait(false);
        var length = AdbWire.ReadInt32LittleEndian(lengthBuffer);
        return await ReadTextAsync(length, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // ignore
        }

        await ValueTask.CompletedTask;
    }
}

/// <summary>Linux errno 到读写错误的映射。</summary>
internal static class AdbErrno
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int EIO = 5;
    public const int EACCES = 13;
    public const int EBUSY = 16;
    public const int EEXIST = 17;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int ENOSPC = 28;
    public const int EROFS = 30;
    public const int ENOTEMPTY = 39;

    public static AdbErrorKind ToKind(int errno) => errno switch
    {
        ENOENT => AdbErrorKind.PathNotFound,
        EACCES or EPERM => AdbErrorKind.PermissionDenied,
        ENOTDIR => AdbErrorKind.NotADirectory,
        EEXIST => AdbErrorKind.AlreadyExists,
        ENOTEMPTY => AdbErrorKind.DirectoryNotEmpty,
        ENOSPC => AdbErrorKind.DiskFull,
        EISDIR or EINVAL => AdbErrorKind.IoError,
        _ => AdbErrorKind.IoError,
    };

    public static string ToMessage(int errno) => errno switch
    {
        ENOENT => "文件或目录不存在",
        EACCES => "权限不足",
        EPERM => "操作不被允许",
        ENOTDIR => "路径中包含非目录项",
        EISDIR => "目标是目录",
        EEXIST => "目标已存在",
        ENOTEMPTY => "目录非空",
        ENOSPC => "设备空间不足",
        EROFS => "只读文件系统",
        EBUSY => "设备忙",
        EIO => "I/O 错误",
        _ => $"错误码 {errno}",
    };
}
