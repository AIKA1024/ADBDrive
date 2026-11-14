using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.Localization;
using DokanNet;
using DokanNet.Logging;

namespace ADBDrive.Core.Mount;

/// <summary>Dokan 状态：保留词条 key，界面按当前语言再取文本（切换语言后能跟着变）。</summary>
public readonly record struct DokanStatusInfo(bool Installed, string Key, object?[] Args)
{
    public string Text => Loc.F(Key, Args);

    public override string ToString() => Text;
}

/// <summary>Dokan 驱动安装状态检测。</summary>
public static class DokanSupport
{
    /// <summary>驱动是否可用（dokan2.dll 已安装且驱动版本 &gt; 0）。</summary>
    public static DokanStatusInfo Check()
    {
        try
        {
            using var dokan = new Dokan(new NullLogger());
            var driverVersion = dokan.DriverVersion;
            var libraryVersion = dokan.Version;
            if (driverVersion <= 0)
            {
                return new DokanStatusInfo(false, "dokan.notStarted", [libraryVersion]);
            }

            return new DokanStatusInfo(
                true,
                "dokan.ready",
                [FormatVersion(libraryVersion), FormatVersion(driverVersion)]);
        }
        catch (DllNotFoundException)
        {
            return new DokanStatusInfo(false, "dokan.dllMissing", []);
        }
        catch (Exception ex)
        {
            return new DokanStatusInfo(false, "dokan.unavailable", [ex.Message]);
        }
    }

    /// <summary>驱动是否可用，并给出当前语言的说明文本。</summary>
    public static bool IsInstalled(out string message)
    {
        var status = Check();
        message = status.Text;
        return status.Installed;
    }

    private static string FormatVersion(int version) => $"{version / 100}.{(version % 100) / 10}.{version % 10}";
}

/// <summary>盘符工具。</summary>
public static class DriveLetters
{
    private static readonly char[] PreferredOrder = ['Z', 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M', 'L', 'K', 'J', 'I', 'H', 'G', 'F', 'E', 'D'];

    /// <summary>已占用的盘符（含网络盘 / 光驱）。</summary>
    public static HashSet<char> GetUsed()
    {
        var used = new HashSet<char>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.Name.Length >= 1)
                {
                    used.Add(char.ToUpperInvariant(drive.Name[0]));
                }
            }
            catch
            {
                // 某些驱动器枚举时会抛异常，忽略。
            }
        }

        return used;
    }

    public static bool IsFree(char letter)
    {
        letter = char.ToUpperInvariant(letter);
        if (letter is < 'D' or > 'Z')
        {
            return false;
        }

        return !GetUsed().Contains(letter);
    }

    /// <summary>优先返回指定盘符；被占用时按 Z→D 的顺序找一个空闲盘符。</summary>
    public static char? FindFree(char preferred = '\0')
    {
        var used = GetUsed();
        if (preferred != '\0')
        {
            var normalized = char.ToUpperInvariant(preferred);
            if (normalized is >= 'D' and <= 'Z' && !used.Contains(normalized))
            {
                return normalized;
            }
        }

        foreach (var letter in PreferredOrder)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        return null;
    }
}

/// <summary>
/// 一次 Dokan 挂载的生命周期封装：创建实例 -> 挂载 -> 卸载。
/// 卸载时会等待内核侧句柄释放，避免资源管理器卡住。
/// </summary>
public sealed class DokanMountSession : IDisposable
{
    private readonly AppLog _log;
    private Dokan? _dokan;
    private DokanInstance? _instance;
    private bool _disposed;

    public DokanMountSession(char driveLetter, AppLog log)
    {
        DriveLetter = char.ToUpperInvariant(driveLetter);
        _log = log;
    }

    public char DriveLetter { get; }

    public string MountPoint => $"{DriveLetter}:";

    public bool IsMounted => _instance is not null && !_disposed;

    /// <summary>同步挂载。失败会抛 DokanException。</summary>
    public void Mount(AdbDokanOperations operations, int timeoutSeconds = 45)
    {
        if (_instance is not null)
        {
            throw new InvalidOperationException("该会话已经挂载。");
        }

        var logger = new DokanLogBridge(_log);
        _dokan = new Dokan(logger);
        _log.Info("挂载", $"Dokan 库版本 {_dokan.Version}，驱动版本 {_dokan.DriverVersion}，挂载点 {MountPoint}");

        // 上次异常退出（或被强杀）可能残留一个「僵尸盘符」：字母还在，但没有任何进程响应。
        // 挂载前先尝试清理，否则新挂载会失败或表现为所有操作都拒绝访问。
        try
        {
            if (_dokan.RemoveMountPoint(MountPoint))
            {
                _log.Warn("挂载", $"清理了残留挂载点 {MountPoint}");
            }
        }
        catch (Exception ex)
        {
            _log.Debug("挂载", $"清理残留挂载点失败: {ex.Message}");
        }

        var builder = new DokanInstanceBuilder(_dokan);
        builder.ConfigureOptions(options =>
        {
            options.Options = DokanOptions.FixedDrive;
            options.MountPoint = MountPoint;
            options.TimeOut = TimeSpan.FromSeconds(timeoutSeconds);
            options.AllocationUnitSize = 4096;
            options.SectorSize = 512;
        });

        try
        {
            _instance = builder.Build(operations);
        }
        catch (DokanException ex)
        {
            _log.Error("挂载", $"挂载失败 {MountPoint}: {ex.Message} (status={ex.ErrorStatus})");
            Dispose();
            throw;
        }

        _log.Info("挂载", $"已挂载到 {MountPoint}，卷标 {operations.FileSystem.Root}");
    }

    /// <summary>卸载。句柄未释放时会等待一段时间。</summary>
    public Task UnmountAsync(TimeSpan timeout) => Task.Run(() =>
    {
        if (_instance is null)
        {
            return;
        }

        var instance = _instance;
        _instance = null;
        try
        {
            instance.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn("挂载", $"Dispose 挂载实例异常: {ex.Message}");
        }

        try
        {
            var waited = instance.WaitForFileSystemClosed((uint)Math.Max(0, timeout.TotalMilliseconds));
            if (waited != 0)
            {
                _log.Warn("挂载", $"等待文件系统关闭超时（剩余挂起请求 {waited}）");
            }
        }
        catch (Exception ex)
        {
            _log.Debug("挂载", $"WaitForFileSystemClosed 异常: {ex.Message}");
        }

        try
        {
            _dokan?.RemoveMountPoint(MountPoint);
        }
        catch
        {
            // ignore
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _instance?.Dispose();
        }
        catch
        {
            // ignore
        }

        _instance = null;
        try
        {
            _dokan?.Dispose();
        }
        catch
        {
            // ignore
        }

        _dokan = null;
    }
}

/// <summary>把 Dokan 自己的日志接到应用日志里。</summary>
internal sealed class DokanLogBridge : ILogger
{
    private readonly AppLog _log;

    public DokanLogBridge(AppLog log) => _log = log;

    public bool DebugEnabled => true;

    public void Debug(string message, params object[] args) => _log.Debug("Dokan", Format(message, args));

    public void Info(string message, params object[] args) => _log.Debug("Dokan", Format(message, args));

    public void Warn(string message, params object[] args) => _log.Warn("Dokan", Format(message, args));

    public void Error(string message, params object[] args) => _log.Debug("Dokan", Format(message, args));

    public void Fatal(string message, params object[] args) => _log.Error("Dokan", Format(message, args));

    public void Dispose()
    {
    }

    private static string Format(string message, object[] args)
    {
        try
        {
            return args is { Length: > 0 } ? string.Format(message, args) : message;
        }
        catch
        {
            return message;
        }
    }
}
