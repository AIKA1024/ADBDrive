using DokanNet;
using DokanFileAccess = DokanNet.FileAccess;
using ADBDrive.Core.FileSystem;

namespace ADBDrive.Core.Mount;

/// <summary>
/// 一个打开的句柄。
/// 读句柄直接持有 adb RECV 流（顺序读，命中缓存时零往返）；
/// 写句柄先在本地临时文件里累积，Flush/Close 时整体 push——这样乱序写、随机写都不会损坏数据。
/// </summary>
internal sealed class OpenHandle : IDisposable
{
    public required string DevicePath { get; init; }

    public bool IsDirectory { get; init; }

    public DokanFileAccess Access { get; init; }

    public bool CanDelete { get; set; }

    public bool DeleteOnClose { get; set; }

    /// <summary>写入是否总是追加到末尾。</summary>
    public bool WriteToEnd { get; set; }

    /// <summary>打开时文件已存在的长度。</summary>
    public long BaseLength { get; set; }

    /// <summary>打开时文件不存在（写新文件）。</summary>
    public bool BaseMissing { get; set; }

    /// <summary>是否以截断方式打开（CREATE_ALWAYS / TRUNCATE_EXISTING）。</summary>
    public bool Truncated { get; set; }

    public string? TempPath { get; set; }

    public FileStream? TempStream { get; set; }

    public long WriteLength { get; set; }

    public bool Dirty { get; set; }

    public Stream? ReadStream { get; set; }

    public long ReadPosition { get; set; }

    public bool Materialized { get; set; }

    public bool DisposeRequested { get; set; }

    public object Gate { get; } = new();

    public bool WantsWrite => (Access & (DokanFileAccess.WriteData | DokanFileAccess.AppendData)) != 0
                              || (Access & DokanFileAccess.GenericWrite) != 0
                              || (Access & DokanFileAccess.GenericAll) != 0;

    public bool WantsRead => (Access & DokanFileAccess.ReadData) != 0
                             || (Access & DokanFileAccess.GenericRead) != 0
                             || (Access & DokanFileAccess.GenericAll) != 0;

    public bool NeedsBaseContent => !BaseMissing && !Truncated;

    /// <summary>确保写缓冲已就绪（必要时先把远端文件下载下来）。</summary>
    public async Task EnsureWritableAsync(AdbFileSystem fs, CancellationToken ct)
    {
        if (TempStream is not null)
        {
            return;
        }

        TempPath ??= Path.Combine(ADBDrive.Core.Paths.AppPaths.CacheDirectory, "write", Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(TempPath)!);
        TempStream = new FileStream(TempPath, FileMode.Create, System.IO.FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.None);

        if (NeedsBaseContent && !Materialized && BaseLength > 0)
        {
            var remote = await fs.OpenReadAsync(DevicePath, BaseLength, ct).ConfigureAwait(false);
            await using (remote)
            {
                await remote.CopyToAsync(TempStream, 128 * 1024, ct).ConfigureAwait(false);
            }

            WriteLength = TempStream.Length;
        }
        else
        {
            WriteLength = 0;
        }

        Materialized = true;
    }

    public void Dispose()
    {
        try
        {
            ReadStream?.Dispose();
        }
        catch
        {
            // ignore
        }

        ReadStream = null;

        try
        {
            TempStream?.Dispose();
        }
        catch
        {
            // ignore
        }

        TempStream = null;

        if (TempPath is not null)
        {
            try
            {
                File.Delete(TempPath);
            }
            catch
            {
                // ignore
            }

            TempPath = null;
        }
    }
}
