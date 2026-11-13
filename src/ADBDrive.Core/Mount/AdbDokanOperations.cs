using System.Security.AccessControl;
using ADBDrive.Core.Adb;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.FileSystem;
using DokanNet;
using DokanFileAccess = DokanNet.FileAccess;

namespace ADBDrive.Core.Mount;

/// <summary>
/// Dokan 回调实现：把 Windows 文件系统语义翻译成对 Android 设备的 ADB 操作。
/// 所有回调都是同步签名（Dokan 的线程池调用），内部异步执行并带超时，
/// 保证设备离线时快速返回错误而不是挂住资源管理器。
/// </summary>
public sealed class AdbDokanOperations : IDokanOperations
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(60);

    private readonly AdbFileSystem _fs;
    private readonly AppLog _log;
    private readonly string _volumeLabel;
    private readonly SemaphoreSlim _concurrency = new(16, 16);

    public AdbDokanOperations(AdbFileSystem fileSystem, AppLog log, string volumeLabel)
    {
        _fs = fileSystem;
        _log = log;
        _volumeLabel = volumeLabel;
    }

    public AdbFileSystem FileSystem => _fs;

    /// <summary>挂载期间被调用最频繁的路径，用于诊断。</summary>
    public long OperationCount { get; private set; }

    public NtStatus CreateFile(string fileName, DokanFileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        => Run("CreateFile", fileName, ct => CreateFileCoreAsync(fileName, access, mode, info, ct));

    private async Task<NtStatus> CreateFileCoreAsync(string fileName, DokanFileAccess access, FileMode mode, IDokanFileInfo info, CancellationToken ct)
    {
        var devicePath = _fs.Mapper.ToDevicePath(fileName);

        if (info.IsDirectory || _fs.Mapper.IsRootPath(fileName))
        {
            return await CreateDirectoryCoreAsync(devicePath, mode, info, ct).ConfigureAwait(false);
        }

        var entry = await _fs.StatAsync(devicePath, ct).ConfigureAwait(false);
        if (entry is { IsDirectory: true })
        {
            // 关键：Win32 的权限位对目录有第二套含义 ——
            //   FILE_READ_DATA(0x1)  == FILE_LIST_DIRECTORY（列目录）
            //   FILE_WRITE_DATA(0x2) == FILE_ADD_FILE（新建子文件）
            //   FILE_APPEND_DATA(0x4) == FILE_ADD_SUBDIRECTORY（新建子目录）
            // Explorer 打开文件夹时发的就是 FILE_LIST_DIRECTORY，它和"读文件内容"
            // 是同一个位，绝不能按"要读写数据"拒绝 —— 否则整个盘符下所有目录都返回
            // STATUS_NOT_A_DIRECTORY，Windows 显示成"位置不可用 / 目录名称无效"。
            // 目录的数据通道本来就会被 ReadFile/WriteFile 用 handle.IsDirectory 挡掉，
            // 这里一律放行。
            info.IsDirectory = true;
            return DokanResult.Success;
        }

        var exists = entry is not null;
        if (!exists)
        {
            if (mode is FileMode.Open or FileMode.Truncate)
            {
                return DokanResult.FileNotFound;
            }

            var parent = GetParent(devicePath);
            var parentEntry = await _fs.StatAsync(parent, ct).ConfigureAwait(false);
            if (parentEntry is not { IsDirectory: true })
            {
                return DokanResult.PathNotFound;
            }

            // 立刻在设备上落一个空文件：
            // 1) 只读文件系统（/、/system、未 remount 的分区）在这里就报错，资源管理器能收到
            //    「介质受写保护」，而不是等句柄关闭写回时才失败 —— Cleanup 没有返回值，
            //    失败只能记日志，用户看到的是"创建成功但文件不存在"；
            // 2) 也符合 Windows「新建即产生空文件」的语义。
            await _fs.CreateFileAsync(devicePath, ct).ConfigureAwait(false);
        }
        else if (mode == FileMode.CreateNew)
        {
            return DokanResult.FileExists;
        }

        var wantsWrite = (access & (DokanFileAccess.WriteData | DokanFileAccess.AppendData | DokanFileAccess.GenericWrite | DokanFileAccess.GenericAll)) != 0;
        var handle = new OpenHandle
        {
            DevicePath = devicePath,
            IsDirectory = false,
            Access = access,
            BaseMissing = !exists,
            BaseLength = entry?.Size ?? 0,
            Truncated = exists && mode is FileMode.Create or FileMode.Truncate,
            DeleteOnClose = info.DeletePending,
            CanDelete = (access & DokanFileAccess.Delete) != 0,
            WriteToEnd = info.WriteToEndOfFile || mode == FileMode.Append,
            WriteLength = entry?.Size ?? 0,
        };

        if (handle.DeleteOnClose && !wantsWrite && access == DokanFileAccess.None)
        {
            // 纯删除句柄，不需要任何数据通道。
        }

        info.Context = handle;
        return DokanResult.Success;
    }

    private async Task<NtStatus> CreateDirectoryCoreAsync(string devicePath, FileMode mode, IDokanFileInfo info, CancellationToken ct)
    {
        info.IsDirectory = true;
        if (_fs.IsRoot(devicePath))
        {
            return DokanResult.Success;
        }

        var entry = await _fs.StatAsync(devicePath, ct).ConfigureAwait(false);
        if (entry is { IsDirectory: true })
        {
            return mode is FileMode.CreateNew ? DokanResult.FileExists : DokanResult.Success;
        }

        if (entry is not null)
        {
            // 目标是文件却要求以目录方式打开。
            return mode is FileMode.CreateNew ? DokanResult.FileExists : DokanResult.NotADirectory;
        }

        if (mode is FileMode.Open or FileMode.Truncate)
        {
            return DokanResult.PathNotFound;
        }

        var parent = GetParent(devicePath);
        var parentEntry = _fs.IsRoot(parent) ? null : await _fs.StatAsync(parent, ct).ConfigureAwait(false);
        if (!_fs.IsRoot(parent) && parentEntry is not { IsDirectory: true })
        {
            return DokanResult.PathNotFound;
        }

        await _fs.CreateDirectoryAsync(devicePath, ct).ConfigureAwait(false);
        return DokanResult.Success;
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        if (info.Context is not OpenHandle handle)
        {
            return DokanResult.InvalidHandle;
        }

        var transferred = 0;
        lock (handle.Gate)
        {
            var status = Run("ReadFile", fileName, async ct =>
            {
                if (handle.TempStream is not null)
                {
                    await handle.EnsureWritableAsync(_fs, ct).ConfigureAwait(false);
                    var temp = handle.TempStream!;
                    if (offset >= temp.Length)
                    {
                        return DokanResult.Success;
                    }

                    temp.Position = offset;
                    transferred = await temp.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    return DokanResult.Success;
                }

                if (handle.ReadStream is null || offset < handle.ReadPosition)
                {
                    handle.ReadStream?.Dispose();
                    handle.ReadStream = await _fs.OpenReadAsync(handle.DevicePath, handle.BaseLength, ct).ConfigureAwait(false);
                    handle.ReadPosition = 0;
                }

                // adb 的 RECV 只能顺序读，需要向前定位时丢弃数据。
                if (offset > handle.ReadPosition)
                {
                    await SkipForwardAsync(handle, offset, ct).ConfigureAwait(false);
                }

                if (offset != handle.ReadPosition)
                {
                    return DokanResult.Success;
                }

                transferred = await handle.ReadStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                handle.ReadPosition += transferred;
                return DokanResult.Success;
            }, TransferTimeout);

            bytesRead = transferred;
            return status;
        }
    }

    private static async Task SkipForwardAsync(OpenHandle handle, long target, CancellationToken ct)
    {
        var stream = handle.ReadStream!;
        var scratch = new byte[128 * 1024];
        while (handle.ReadPosition < target)
        {
            var want = (int)Math.Min(target - handle.ReadPosition, scratch.Length);
            var read = await stream.ReadAsync(scratch.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            handle.ReadPosition += read;
        }
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        if (info.Context is not OpenHandle handle)
        {
            return DokanResult.InvalidHandle;
        }

        if (!handle.WantsWrite && !handle.WriteToEnd)
        {
            return DokanResult.AccessDenied;
        }

        lock (handle.Gate)
        {
            var status = Run("WriteFile", fileName, async ct =>
            {
                await handle.EnsureWritableAsync(_fs, ct).ConfigureAwait(false);
                var temp = handle.TempStream!;
                var position = handle.WriteToEnd ? handle.WriteLength : offset;
                temp.Position = position;
                await temp.WriteAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                handle.WriteLength = Math.Max(handle.WriteLength, position + buffer.Length);
                handle.Dirty = true;
                return DokanResult.Success;
            }, TransferTimeout);

            if (status == DokanResult.Success)
            {
                bytesWritten = buffer.Length;
            }

            return status;
        }
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        if (info.Context is not OpenHandle handle || !handle.Dirty)
        {
            return DokanResult.Success;
        }

        lock (handle.Gate)
        {
            return Run("Flush", fileName, async ct =>
            {
                await PushAsync(handle, ct).ConfigureAwait(false);
                return DokanResult.Success;
            }, TransferTimeout);
        }
    }

    private async Task PushAsync(OpenHandle handle, CancellationToken ct)
    {
        if (!handle.Dirty || handle.TempStream is null)
        {
            return;
        }

        try
        {
            var temp = handle.TempStream;
            await temp.FlushAsync(ct).ConfigureAwait(false);
            temp.Position = 0;
            await _fs.PushAsync(handle.DevicePath, temp, ct, DateTimeOffset.Now).ConfigureAwait(false);
            handle.Dirty = false;
            handle.BaseLength = handle.WriteLength;
            handle.BaseMissing = false;
            handle.Truncated = false;
        }
        catch (Exception ex)
        {
            // 数据已经完整落在本地临时文件里，失败后仍可重试，不能直接丢掉。
            _log.Error("挂载", $"写入设备失败 {handle.DevicePath}: {ex.Message}（本地副本: {handle.TempPath}）");
            throw;
        }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        if (info.Context is not OpenHandle handle)
        {
            return DokanResult.NotImplemented;
        }

        lock (handle.Gate)
        {
            return Run("SetEndOfFile", fileName, async ct =>
            {
                await handle.EnsureWritableAsync(_fs, ct).ConfigureAwait(false);
                var temp = handle.TempStream!;
                if (length < temp.Length)
                {
                    temp.SetLength(length);
                }
                else if (length > temp.Length)
                {
                    temp.SetLength(length);
                }

                handle.WriteLength = length;
                handle.Dirty = true;
                return DokanResult.Success;
            }, TransferTimeout);
        }
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        fileInfo = default;
        var devicePath = _fs.Mapper.ToDevicePath(fileName);
        var result = default(FileInformation);

        var status = Run("GetFileInformation", fileName, async ct =>
        {
            if (info.Context is OpenHandle dirtyHandle && dirtyHandle.WriteLength > 0 && dirtyHandle.Dirty)
            {
                result = new FileInformation
                {
                    FileName = GetLeaf(fileName),
                    Attributes = FileAttributes.Normal,
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Length = dirtyHandle.WriteLength,
                };
                return DokanResult.Success;
            }

            var entry = await _fs.StatAsync(devicePath, ct).ConfigureAwait(false);
            if (entry is null)
            {
                return DokanResult.FileNotFound;
            }

            result = ToFileInformation(entry.Value, GetLeaf(fileName));
            return DokanResult.Success;
        });

        if (status == DokanResult.Success)
        {
            fileInfo = result;
        }

        return status;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        => FindFilesCore(fileName, null, out files);

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
        => FindFilesCore(fileName, searchPattern, out files);

    private NtStatus FindFilesCore(string fileName, string? searchPattern, out IList<FileInformation> files)
    {
        files = Array.Empty<FileInformation>();
        var devicePath = _fs.Mapper.ToDevicePath(fileName);
        IList<FileInformation>? collected = null;

        var status = Run("FindFiles", fileName, async ct =>
        {
            var entries = await _fs.ListAsync(devicePath, ct).ConfigureAwait(false);
            // 不能返回 "." 和 ".."：Dokan 驱动按 NT 语义自行填充这两项，
            // 用户态再返回一遍会让资源管理器把它们当成真实子项。
            var result = new List<FileInformation>(entries.Count);

            foreach (var entry in entries)
            {
                if (searchPattern is not null && !WildcardMatcher.IsMatch(searchPattern, entry.Name))
                {
                    continue;
                }

                result.Add(ToFileInformation(entry, entry.Name));
            }

            collected = result;
            return DokanResult.Success;
        });

        if (status == DokanResult.Success && collected is not null)
        {
            files = collected;
        }

        return status;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        var devicePath = _fs.Mapper.ToDevicePath(fileName);
        return Run("DeleteFile", fileName, async ct =>
        {
            if (info.Context is OpenHandle handle)
            {
                lock (handle.Gate)
                {
                    handle.Dispose();
                }

                info.Context = null;
            }

            await _fs.DeleteFileAsync(devicePath, ct).ConfigureAwait(false);
            return DokanResult.Success;
        });
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        var devicePath = _fs.Mapper.ToDevicePath(fileName);
        return Run("DeleteDirectory", fileName, async ct =>
        {
            await _fs.DeleteDirectoryAsync(devicePath, ct).ConfigureAwait(false);
            return DokanResult.Success;
        });
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        var from = _fs.Mapper.ToDevicePath(oldName);
        var to = _fs.Mapper.ToDevicePath(newName);
        return Run("MoveFile", oldName, async ct =>
        {
            await _fs.MoveAsync(from, to, replace, ct).ConfigureAwait(false);
            if (info.Context is OpenHandle handle)
            {
                handle.BaseMissing = false;
            }

            return DokanResult.Success;
        });
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        var devicePath = _fs.Mapper.ToDevicePath(fileName);
        var target = lastWriteTime ?? lastAccessTime ?? creationTime;
        if (target is null)
        {
            return DokanResult.Success;
        }

        return Run("SetFileTime", fileName, async ct =>
        {
            await _fs.SetLastWriteTimeAsync(devicePath, new DateTimeOffset(DateTime.SpecifyKind(target.Value, DateTimeKind.Utc)), ct).ConfigureAwait(false);
            return DokanResult.Success;
        });
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        var devicePath = _fs.Mapper.ToDevicePath(fileName);
        var readOnly = (attributes & FileAttributes.ReadOnly) != 0;
        return Run("SetFileAttributes", fileName, async ct =>
        {
            var entry = await _fs.StatAsync(devicePath, ct).ConfigureAwait(false);
            if (entry is null)
            {
                return DokanResult.FileNotFound;
            }

            var isDirectory = entry.Value.IsDirectory;
            var mode = readOnly
                ? (isDirectory ? "0555" : "0444")
                : (isDirectory ? "0755" : "0644");
            await _fs.Device.ExecuteAsync($"chmod {mode} {DevicePathMapper.Quote(devicePath)}", ct).ConfigureAwait(false);
            _fs.Invalidate(devicePath);
            return DokanResult.Success;
        });
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        freeBytesAvailable = 0;
        totalNumberOfBytes = 0;
        totalNumberOfFreeBytes = 0;

        var space = default(DiskSpace);
        var status = Run("GetDiskFreeSpace", "\\", async ct =>
        {
            space = await _fs.GetDiskSpaceAsync(ct).ConfigureAwait(false);
            return space.TotalBytes > 0 ? DokanResult.Success : DokanResult.NotReady;
        });

        if (status == DokanResult.Success)
        {
            freeBytesAvailable = space.FreeBytes;
            totalNumberOfBytes = space.TotalBytes;
            totalNumberOfFreeBytes = space.FreeBytes;
        }

        return status;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = _volumeLabel;
        fileSystemName = "ADBFS";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk | FileSystemFeatures.CaseSensitiveSearch;
        return DokanResult.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info) => DokanResult.NotImplemented;

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        _log.Info("挂载", $"卷已就绪: {mountPoint}");
        return DokanResult.Success;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        _log.Info("挂载", "卷已卸载");
        return DokanResult.Success;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = Array.Empty<FileInformation>();
        return DokanResult.Success;
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        if (info.Context is not OpenHandle handle)
        {
            if (info.DeletePending)
            {
                // 目录没有句柄上下文，删除请求直接走一次设备调用。
                var devicePath = _fs.Mapper.ToDevicePath(fileName);
                Run("DeleteOnClose", fileName, async ct =>
                {
                    if (info.IsDirectory)
                    {
                        await _fs.DeleteDirectoryAsync(devicePath, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await _fs.DeleteFileAsync(devicePath, ct).ConfigureAwait(false);
                    }

                    return DokanResult.Success;
                });
            }

            return;
        }

        lock (handle.Gate)
        {
            try
            {
                if (info.DeletePending)
                {
                    Run("DeleteOnClose", fileName, async ct =>
                    {
                        if (handle.IsDirectory)
                        {
                            await _fs.DeleteDirectoryAsync(handle.DevicePath, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await _fs.DeleteFileAsync(handle.DevicePath, ct).ConfigureAwait(false);
                        }

                        return DokanResult.Success;
                    });
                }
                else if (handle.Dirty)
                {
                    var status = Run("CleanupFlush", fileName, async ct =>
                    {
                        await PushAsync(handle, ct).ConfigureAwait(false);
                        return DokanResult.Success;
                    }, TransferTimeout);
                    if (status != DokanResult.Success)
                    {
                        _log.Warn("挂载", $"文件 {handle.DevicePath} 未能在关闭前写回设备");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("挂载", "Cleanup 失败", ex);
            }
            finally
            {
                try
                {
                    handle.ReadStream?.Dispose();
                }
                catch
                {
                    // ignore
                }

                handle.ReadStream = null;
            }
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        if (info.Context is not OpenHandle handle)
        {
            return;
        }

        info.Context = null;
        lock (handle.Gate)
        {
            handle.Dispose();
        }
    }

    private static FileInformation ToFileInformation(AdbEntry entry, string displayName)
    {
        var attributes = entry.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        if (entry.IsHidden)
        {
            attributes |= FileAttributes.Hidden;
        }

        if (!entry.IsDirectory && !entry.IsWritable)
        {
            attributes |= FileAttributes.ReadOnly;
        }

        return new FileInformation
        {
            FileName = displayName,
            Attributes = attributes,
            CreationTime = entry.Modified.UtcDateTime,
            LastAccessTime = entry.Modified.UtcDateTime,
            LastWriteTime = entry.Modified.UtcDateTime,
            Length = entry.IsDirectory ? 0 : entry.Size,
        };
    }

    private static string GetParent(string devicePath)
    {
        var trimmed = devicePath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static string GetLeaf(string fileName)
    {
        var trimmed = fileName.TrimEnd('\\', '/');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    /// <summary>统一的错误处理 / 超时 / 并发控制入口。</summary>
    private NtStatus Run(string operation, string path, Func<CancellationToken, Task<NtStatus>> body, TimeSpan? timeout = null)
    {
        OperationCount++;
        if (!_concurrency.Wait(TimeSpan.FromSeconds(30)))
        {
            _log.Warn("挂载", $"{operation} 并发过高，主动拒绝: {path}");
            return DokanResult.NotReady;
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout ?? MetadataTimeout);
            var status = body(cts.Token).GetAwaiter().GetResult();
            if (status != DokanResult.Success && status != DokanResult.NotImplemented)
            {
                _log.Debug("挂载", $"{operation} {path} => {status}");
            }

            return status;
        }
        catch (OperationCanceledException)
        {
            _log.Warn("挂载", $"{operation} 超时: {path}");
            return NtStatus.IoTimeout;
        }
        catch (AdbException ex)
        {
            var status = NtStatusMapper.FromAdbKind(ex.Kind);
            if (ex.Kind is AdbErrorKind.Offline or AdbErrorKind.ServerUnavailable or AdbErrorKind.DeviceNotFound or AdbErrorKind.ConnectionFailed)
            {
                _log.Warn("挂载", $"{operation} 设备不可用: {path} ({ex.Message})");
            }
            else
            {
                _log.Debug("挂载", $"{operation} 失败: {path} => {ex.Kind} {ex.Message}");
            }

            return status;
        }
        catch (Exception ex)
        {
            _log.Error("挂载", $"{operation} 异常: {path}", ex);
            return NtStatusMapper.From(ex);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
