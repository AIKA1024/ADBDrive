using ADBDrive.Core.Adb;
using DokanNet;

namespace ADBDrive.Core.Mount;

/// <summary>把 ADB 层异常翻译成 Windows 标准错误码，避免资源管理器卡住或弹「未知错误」。</summary>
internal static class NtStatusMapper
{
    /// <summary>
    /// STATUS_MEDIA_WRITE_PROTECTED。DokanNet 的 NtStatus 枚举没收录这个值，
    /// 但 Windows 认它 —— 会显示「介质受写保护」，比笼统的「参数错误」准确得多。
    /// </summary>
    private const NtStatus MediaWriteProtected = (NtStatus)unchecked((int)0xC00000A2);

    public static NtStatus From(Exception exception)
    {
        switch (exception)
        {
            case AdbException adb:
                return FromAdbKind(adb.Kind);
            case OperationCanceledException:
                return NtStatus.IoTimeout;
            case TimeoutException:
                return NtStatus.IoTimeout;
            case UnauthorizedAccessException:
                return DokanResult.AccessDenied;
            case FileNotFoundException:
                return DokanResult.FileNotFound;
            case DirectoryNotFoundException:
                return DokanResult.PathNotFound;
            case IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase):
                return DokanResult.DiskFull;
            case IOException:
                return DokanResult.Error;
            default:
                return DokanResult.Error;
        }
    }

    public static NtStatus FromAdbKind(Adb.AdbErrorKind kind) => kind switch
    {
        Adb.AdbErrorKind.PathNotFound => DokanResult.PathNotFound,
        Adb.AdbErrorKind.PermissionDenied => DokanResult.AccessDenied,
        Adb.AdbErrorKind.ReadOnly => MediaWriteProtected,
        Adb.AdbErrorKind.Unauthorized => DokanResult.AccessDenied,
        Adb.AdbErrorKind.NotADirectory => DokanResult.NotADirectory,
        Adb.AdbErrorKind.AlreadyExists => DokanResult.FileExists,
        Adb.AdbErrorKind.DirectoryNotEmpty => DokanResult.DirectoryNotEmpty,
        Adb.AdbErrorKind.DiskFull => DokanResult.DiskFull,
        Adb.AdbErrorKind.ServerUnavailable => DokanResult.NotReady,
        Adb.AdbErrorKind.ConnectionFailed => DokanResult.NotReady,
        Adb.AdbErrorKind.Offline => DokanResult.NotReady,
        Adb.AdbErrorKind.DeviceNotFound => DokanResult.NotReady,
        Adb.AdbErrorKind.Timeout => NtStatus.IoTimeout,
        Adb.AdbErrorKind.Cancelled => NtStatus.IoTimeout,
        Adb.AdbErrorKind.ProtocolError => DokanResult.Error,
        Adb.AdbErrorKind.IoError => DokanResult.Error,
        _ => DokanResult.Error,
    };
}
