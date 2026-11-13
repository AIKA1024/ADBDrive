using ADBDrive.Core.Adb;
using ADBDrive.Core.Diagnostics;

namespace ADBDrive.Core.FileSystem;

/// <summary>目录 / 属性缓存项。</summary>
internal sealed class CacheItem<T>
{
    public CacheItem(T value, DateTime expiresAtUtc)
    {
        Value = value;
        ExpiresAtUtc = expiresAtUtc;
    }

    public T Value { get; }

    public DateTime ExpiresAtUtc { get; }
}

/// <summary>
/// 面向 Dokan 的高层文件系统封装：负责缓存、shell 命令拼装、错误分类。
/// 所有方法都使用设备绝对路径（/sdcard/...）。
/// </summary>
public sealed class AdbFileSystem
{
    private static readonly TimeSpan MetadataTtl = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MissTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DirectoryTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SpaceTtl = TimeSpan.FromSeconds(30);

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CacheItem<AdbEntry?>> _statCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheItem<List<AdbEntry>>> _dirCache = new(StringComparer.Ordinal);
    private CacheItem<DiskSpace>? _spaceCache;
    private string? _deviceTimeZone;

    public AdbFileSystem(AdbDeviceConnection device, DevicePathMapper mapper, AppLog log)
    {
        Device = device;
        Mapper = mapper;
        Log = log;
    }

    public AdbDeviceConnection Device { get; }

    public DevicePathMapper Mapper { get; }

    public AppLog Log { get; }

    public string Root => Mapper.Root;

    /// <summary>缓存 TTL 的全局缩放（0 表示禁用缓存）。</summary>
    public double CacheScale { get; set; } = 1.0;

    public async Task<AdbEntry?> StatAsync(string path, CancellationToken ct, bool bypassCache = false)
    {
        if (IsRoot(path))
        {
            return new AdbEntry("/", Root, true, false, 0, DateTimeOffset.Now, 0x41ED);
        }

        if (!bypassCache && TryGetStat(path, out var cached))
        {
            return cached;
        }

        await using var sync = await Device.OpenSyncAsync(ct).ConfigureAwait(false);
        var entry = await sync.StatAsync(path, ct).ConfigureAwait(false);
        StoreStat(path, entry);
        return entry;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        var entry = await StatAsync(path, ct).ConfigureAwait(false);
        return entry is not null;
    }

    /// <summary>
    /// 校验挂载根路径是否真实存在（绕过「根路径一律当目录」的快捷判断）。
    /// 返回 null 表示不存在；符号链接也算可用（例如 /sdcard 指向 /storage/emulated/0）。
    /// </summary>
    public async Task<AdbEntry?> ProbeRootAsync(CancellationToken ct)
    {
        await using var sync = await Device.OpenSyncAsync(ct).ConfigureAwait(false);
        var entry = await sync.StatAsync(Root, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        // 根必须是目录或指向目录的符号链接；普通文件不能当挂载点。
        if (!entry.Value.IsDirectory && !entry.Value.IsSymlink)
        {
            return null;
        }

        return entry;
    }

    public async Task<IReadOnlyList<AdbEntry>> ListAsync(string path, CancellationToken ct, bool bypassCache = false)
    {
        if (!bypassCache && TryGetDirectory(path, out var cached))
        {
            return cached;
        }

        await using var sync = await Device.OpenSyncAsync(ct).ConfigureAwait(false);
        var entries = await sync.ListAsync(path, ct).ConfigureAwait(false);
        StoreDirectory(path, entries);
        return entries;
    }

    /// <summary>打开只读流；返回的流由调用方释放。</summary>
    public async Task<Stream> OpenReadAsync(string path, long sizeHint, CancellationToken ct)
    {
        var sync = await Device.OpenSyncAsync(ct).ConfigureAwait(false);
        try
        {
            return await sync.OpenReadAsync(path, sizeHint, ct).ConfigureAwait(false);
        }
        catch
        {
            await sync.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>把本地流写入设备文件（覆盖）。</summary>
    public async Task PushAsync(string path, Stream source, CancellationToken ct, DateTimeOffset? modified = null)
    {
        await using var sync = await Device.OpenSyncAsync(ct).ConfigureAwait(false);
        await sync.PushAsync(path, source, modified, ct).ConfigureAwait(false);
        Invalidate(path);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var existing = await StatAsync(path, ct, bypassCache: true).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Value.IsDirectory)
            {
                return;
            }

            throw new AdbException(AdbErrorKind.AlreadyExists, $"已存在同名文件: {path}");
        }

        await Device.ExecuteAsync($"mkdir -p {DevicePathMapper.Quote(path)}", ct).ConfigureAwait(false);
        Invalidate(path);
    }

    /// <summary>
    /// 在设备上创建一个空文件（已存在则保留原内容）。
    /// 新建文件时先调这个，让"只读文件系统"这类错误在打开阶段就暴露 ——
    /// 否则会一直拖到句柄关闭写回时才失败，而 Cleanup 回调没有返回值，用户只会看到文件莫名消失。
    /// </summary>
    public async Task CreateFileAsync(string path, CancellationToken ct)
    {
        await Device.ExecuteAsync($"touch {DevicePathMapper.Quote(path)}", ct).ConfigureAwait(false);
        Invalidate(path);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        await Device.ExecuteAsync($"rm -f {DevicePathMapper.Quote(path)}", ct).ConfigureAwait(false);
        Invalidate(path);
    }

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        await Device.ExecuteAsync($"rmdir {DevicePathMapper.Quote(path)}", ct).ConfigureAwait(false);
        Invalidate(path);
    }

    public async Task MoveAsync(string source, string destination, bool replace, CancellationToken ct)
    {
        var target = await StatAsync(destination, ct, bypassCache: true).ConfigureAwait(false);
        if (target is not null)
        {
            if (!replace)
            {
                throw new AdbException(AdbErrorKind.AlreadyExists, $"目标已存在: {destination}");
            }

            if (target.Value.IsDirectory)
            {
                // mv 会把源移动到目标目录里，这不是 Windows 期望的语义。
                throw new AdbException(AdbErrorKind.AlreadyExists, $"目标目录已存在: {destination}");
            }
        }

        await Device.ExecuteAsync(
            $"mv -f {DevicePathMapper.Quote(source)} {DevicePathMapper.Quote(destination)}", ct).ConfigureAwait(false);
        Invalidate(source);
        Invalidate(destination);
    }

    /// <summary>设置修改时间。失败不抛异常（只影响显示的时间戳）。</summary>
    public async Task SetLastWriteTimeAsync(string path, DateTimeOffset utcTime, CancellationToken ct)
    {
        var epoch = utcTime.ToUnixTimeSeconds();
        var quoted = DevicePathMapper.Quote(path);
        try
        {
            var result = await Device.ShellAsync($"touch -m -d @{epoch} {quoted}", ct).ConfigureAwait(false);
            if (!result.Success)
            {
                // 老版本 toybox 不支持 @epoch，退化为 POSIX touch -t（设备本地时间）。
                if (_deviceTimeZone is null)
                {
                    var tz = await Device.ShellAsync("date +%z", ct).ConfigureAwait(false);
                    _deviceTimeZone = tz.StandardOutput.Trim();
                }

                var offset = ParseUtcOffset(_deviceTimeZone);
                var local = utcTime.ToOffset(offset);
                await Device.ExecuteAsync($"touch -m -t {local:yyyyMMddHHmm.ss} {quoted}", ct).ConfigureAwait(false);
            }
        }
        catch (AdbException ex) when (ex.Kind == AdbErrorKind.PathNotFound)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("FS", $"设置时间失败 {path}: {ex.Message}");
        }

        Invalidate(path);
    }

    public async Task<DiskSpace> GetDiskSpaceAsync(CancellationToken ct, bool bypassCache = false)
    {
        if (!bypassCache)
        {
            lock (_cacheGate)
            {
                if (_spaceCache is not null && _spaceCache.ExpiresAtUtc > DateTime.UtcNow)
                {
                    return _spaceCache.Value;
                }
            }
        }

        var result = await Device.ShellAsync($"df -k {DevicePathMapper.Quote(Root)}", ct).ConfigureAwait(false);
        var space = ParseDiskSpace(result.Combined);
        lock (_cacheGate)
        {
            _spaceCache = new CacheItem<DiskSpace>(space, DateTime.UtcNow + Scale(SpaceTtl));
        }

        return space;
    }

    public static DiskSpace ParseDiskSpace(string output)
    {
        long total = 0, free = 0;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            if (long.TryParse(parts[1], out var blocks) && long.TryParse(parts[3], out var available))
            {
                total = blocks * 1024;
                free = available * 1024;
            }
        }

        return new DiskSpace(free, total);
    }

    private static TimeSpan ParseUtcOffset(string text)
    {
        // 形如 +0800 / -0430
        if (text.Length == 5 && (text[0] == '+' || text[0] == '-') &&
            int.TryParse(text.AsSpan(1, 2), out var hours) && int.TryParse(text.AsSpan(3, 2), out var minutes))
        {
            var offset = new TimeSpan(hours, minutes, 0);
            return text[0] == '-' ? -offset : offset;
        }

        return TimeSpan.Zero;
    }

    public bool IsRoot(string path)
    {
        var normalized = path.TrimEnd('/');
        return normalized.Length == 0 || string.Equals(normalized, Root, StringComparison.Ordinal);
    }

    /// <summary>让某个路径的缓存失效（同时失效父目录列表）。</summary>
    public void Invalidate(string path)
    {
        var parent = AdbSyncConnection.GetParentPath(path);
        lock (_cacheGate)
        {
            _statCache.Remove(path);
            _dirCache.Remove(path);
            _statCache.Remove(parent);
            _dirCache.Remove(parent);
            // 父目录的属性（大小 / 时间）也变了。
            _dirCache.Remove(AdbSyncConnection.GetParentPath(parent));
            _spaceCache = null;
        }
    }

    public void InvalidateAll()
    {
        lock (_cacheGate)
        {
            _statCache.Clear();
            _dirCache.Clear();
            _spaceCache = null;
        }
    }

    private bool TryGetStat(string path, out AdbEntry? entry)
    {
        lock (_cacheGate)
        {
            if (_statCache.TryGetValue(path, out var item) && item.ExpiresAtUtc > DateTime.UtcNow)
            {
                entry = item.Value;
                return true;
            }
        }

        entry = null;
        return false;
    }

    private void StoreStat(string path, AdbEntry? entry)
    {
        lock (_cacheGate)
        {
            var ttl = entry is null ? MissTtl : MetadataTtl;
            _statCache[path] = new CacheItem<AdbEntry?>(entry, DateTime.UtcNow + Scale(ttl));
        }
    }

    private bool TryGetDirectory(string path, out List<AdbEntry> entries)
    {
        lock (_cacheGate)
        {
            if (_dirCache.TryGetValue(path, out var item) && item.ExpiresAtUtc > DateTime.UtcNow)
            {
                entries = item.Value;
                return true;
            }
        }

        entries = [];
        return false;
    }

    private void StoreDirectory(string path, List<AdbEntry> entries)
    {
        lock (_cacheGate)
        {
            _dirCache[path] = new CacheItem<List<AdbEntry>>(entries, DateTime.UtcNow + Scale(DirectoryTtl));
        }
    }

    private TimeSpan Scale(TimeSpan ttl)
    {
        var scale = CacheScale;
        if (scale <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks((long)(ttl.Ticks * scale));
    }
}
