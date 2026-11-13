using System.Buffers.Binary;
using System.Text;

namespace ADBDrive.Core.Adb;

/// <summary>
/// 把 ADB sync 的 DATA 帧包装成 .NET 只读流：全程流式，不会把整个文件读进内存。
/// </summary>
internal sealed class AdbRecvStream : Stream
{
    private const int MaxFrameSize = 4 * 1024 * 1024;

    private readonly Stream _socket;
    private readonly AdbSyncConnection _owner;
    private readonly long _length;
    private byte[] _payload = Array.Empty<byte>();
    private int _payloadOffset;
    private int _payloadCount;
    private bool _eof;
    private bool _disposed;
    private long _position;

    public AdbRecvStream(Stream socket, AdbSyncConnection owner, long length)
    {
        _socket = socket;
        _owner = owner;
        _length = length < 0 ? 0 : length;
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0)
        {
            return ValueTask.FromResult(0);
        }

        return ReadCoreAsync(buffer, ct);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            if (_payloadOffset < _payloadCount)
            {
                var available = _payloadCount - _payloadOffset;
                var take = Math.Min(available, buffer.Length);
                _payload.AsMemory(_payloadOffset, take).CopyTo(buffer);
                _payloadOffset += take;
                _position += take;
                return take;
            }

            if (_eof || _disposed)
            {
                return 0;
            }

            var header = new byte[8];
            var read = await _socket.ReadAsync(header, ct).ConfigureAwait(false);
            if (read == 0)
            {
                _eof = true;
                return 0;
            }

            if (read < 8)
            {
                await _socket.ReadExactlyAsync(header.AsMemory(read, 8 - read), ct).ConfigureAwait(false);
            }

            var id = Encoding.ASCII.GetString(header, 0, 4);
            var length = AdbWire.ReadInt32LittleEndian(header.AsSpan(4));

            if (id == "DONE")
            {
                _eof = true;
                return 0;
            }

            if (id == AdbWire.Fail)
            {
                var message = string.Empty;
                if (length > 0 && length < 64 * 1024)
                {
                    var buffer2 = new byte[length];
                    await _socket.ReadExactlyAsync(buffer2, ct).ConfigureAwait(false);
                    message = Encoding.UTF8.GetString(buffer2).TrimEnd('\0');
                }

                throw AdbException.FromDeviceMessage(message, "读取文件失败");
            }

            if (id != "DATA")
            {
                throw new AdbException(AdbErrorKind.ProtocolError, $"RECV 应答异常: {id}");
            }

            if (length < 0 || length > MaxFrameSize)
            {
                throw new AdbException(AdbErrorKind.ProtocolError, $"RECV 帧长度异常: {length}");
            }

            if (_payload.Length < length)
            {
                _payload = new byte[length];
            }

            await _socket.ReadExactlyAsync(_payload.AsMemory(0, length), ct).ConfigureAwait(false);
            _payloadOffset = 0;
            _payloadCount = length;
            if (length == 0)
            {
                _eof = true;
                return 0;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                try
                {
                    _socket.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Dispose(true);
        await ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
