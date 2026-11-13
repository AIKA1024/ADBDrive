namespace ADBDrive.Core.Adb;

/// <summary>ADB 设备在 server 中的状态。</summary>
public enum AdbDeviceState
{
    Unknown,
    Offline,
    Unauthorized,
    Device,
    Recovery,
    Bootloader,
    Sideload,
    NoPermissions,
}

/// <summary>adb devices -l 中的一条设备记录。</summary>
public sealed record AdbDeviceInfo(
    string Serial,
    AdbDeviceState State,
    string? Product,
    string? Model,
    string? Device,
    int? TransportId)
{
    public bool IsOnline => State == AdbDeviceState.Device;

    public bool IsTcpEndpoint => Serial.Contains(':');

    /// <summary>用于界面显示的友好名称。</summary>
    public string DisplayName
    {
        get
        {
            var name = Model ?? Device ?? Product;
            return string.IsNullOrWhiteSpace(name) ? Serial : $"{name} ({Serial})";
        }
    }

    public static AdbDeviceState ParseState(string value) => value switch
    {
        "device" => AdbDeviceState.Device,
        "offline" => AdbDeviceState.Offline,
        "unauthorized" => AdbDeviceState.Unauthorized,
        "authorizing" => AdbDeviceState.Unauthorized,
        "recovery" => AdbDeviceState.Recovery,
        "bootloader" => AdbDeviceState.Bootloader,
        "sideload" => AdbDeviceState.Sideload,
        "no permissions" => AdbDeviceState.NoPermissions,
        _ => AdbDeviceState.Unknown,
    };
}

/// <summary>adb connect 的结果。</summary>
public sealed record AdbConnectResult(bool Success, string Endpoint, string Message);

/// <summary>ADB 交互错误分类，最终会映射成 Windows 错误码。</summary>
public enum AdbErrorKind
{
    Unknown,
    /// <summary>连不上本机 adb server，或 adb.exe 不存在。</summary>
    ServerUnavailable,
    /// <summary>连不上远端设备（网络不通 / 端口未开）。</summary>
    ConnectionFailed,
    /// <summary>设备存在但未授权。</summary>
    Unauthorized,
    /// <summary>设备离线。</summary>
    Offline,
    /// <summary>server 里没有这个 serial。</summary>
    DeviceNotFound,
    PathNotFound,
    PermissionDenied,
    /// <summary>文件系统只读（rootfs、未 remount 的 /system）。</summary>
    ReadOnly,
    NotADirectory,
    AlreadyExists,
    DirectoryNotEmpty,
    DiskFull,
    IoError,
    ProtocolError,
    Timeout,
    /// <summary>操作被主动取消。</summary>
    Cancelled,
}

/// <summary>ADB 层抛出的统一异常。</summary>
public sealed class AdbException : Exception
{
    public AdbException(AdbErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public AdbErrorKind Kind { get; }

    /// <summary>从设备返回的错误文本推断错误类型。</summary>
    public static AdbException FromDeviceMessage(string message, string? context = null)
    {
        var kind = ClassifyDeviceMessage(message);
        var text = context is null ? message : $"{context}: {message}";
        return new AdbException(kind, text);
    }

    public static AdbErrorKind ClassifyDeviceMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return AdbErrorKind.Unknown;
        }

        var m = message.ToLowerInvariant();

        // 只读文件系统要排在 permission denied 之前：
        // rootfs / 未 remount 的 /system 返回的是 EROFS，语义是"介质写保护"而不是权限不足。
        if (m.Contains("read-only file system") || m.Contains("read only file system"))
        {
            return AdbErrorKind.ReadOnly;
        }

        if (m.Contains("permission denied") || m.Contains("operation not permitted"))
        {
            return AdbErrorKind.PermissionDenied;
        }

        // 设备级错误要先于通用 "not found" 判断，否则会被误判成路径不存在。
        if (m.Contains("device not found") || m.Contains("no devices") || m.Contains("device offline"))
        {
            return AdbErrorKind.Offline;
        }

        if (m.Contains("unauthorized") || m.Contains("authentication required"))
        {
            return AdbErrorKind.Unauthorized;
        }

        if (m.Contains("no such file or directory") || m.Contains("does not exist") || m.Contains("not found"))
        {
            return AdbErrorKind.PathNotFound;
        }

        if (m.Contains("not a directory"))
        {
            return AdbErrorKind.NotADirectory;
        }

        if (m.Contains("is a directory"))
        {
            return AdbErrorKind.NotADirectory;
        }

        if (m.Contains("directory not empty"))
        {
            return AdbErrorKind.DirectoryNotEmpty;
        }

        if (m.Contains("file exists") || m.Contains("already exists"))
        {
            return AdbErrorKind.AlreadyExists;
        }

        if (m.Contains("no space left") || m.Contains("disk full"))
        {
            return AdbErrorKind.DiskFull;
        }

        if (m.Contains("connection reset") || m.Contains("broken pipe"))
        {
            return AdbErrorKind.IoError;
        }

        return AdbErrorKind.Unknown;
    }
}

/// <summary>设备上的一个目录项 / 文件属性。</summary>
public readonly record struct AdbEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    DateTimeOffset Modified,
    uint Mode = 0)
{
    public bool IsFile => !IsDirectory;

    /// <summary>是否具有可写位（S_IWUSR）。</summary>
    public bool IsWritable => Mode == 0 || (Mode & 0x80) != 0;

    public bool IsHidden => Name.StartsWith('.') && Name.Length > 1;
}

/// <summary>shell 命令执行结果。</summary>
public sealed record AdbShellResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    public string Combined => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : string.IsNullOrEmpty(StandardOutput)
            ? StandardError
            : StandardOutput + "\n" + StandardError;
}

/// <summary>设备磁盘空间。</summary>
public readonly record struct DiskSpace(long FreeBytes, long TotalBytes)
{
    public static readonly DiskSpace Unknown = new(0, 0);
}
