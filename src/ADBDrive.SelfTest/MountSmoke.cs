using System.Diagnostics;
using ADBDrive.Core.Adb;
using ADBDrive.Core.Config;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.Paths;
using ADBDrive.Core.Sessions;

namespace ADBDrive.SelfTest;

/// <summary>
/// 挂载 / 卸载 / 重新挂载的真机冒烟测试。
/// 注意：文件读写必须放在**子进程**里做——托管文件系统的进程自己访问自己的卷，
/// 会被 Dokan 驱动拒绝（AccessDenied），这是框架限制，不是产品缺陷。
/// </summary>
internal static class MountSmoke
{
    public static async Task<int> RunAsync(string endpoint, string root)
    {
        AppPaths.EnsureCreated();
        var log = new AppLog(AppPaths.LogDirectory);
        log.EntryWritten += entry =>
        {
            if (entry.Level >= LogLevel.Info)
            {
                Console.WriteLine("  " + entry.Format());
            }
        };

        var adbPath = AdbLocator.Find();
        var server = new AdbServerClient(log, adbPath);
        var config = new DeviceConfig { Endpoint = endpoint, RootPath = root };
        using var session = new DeviceSession(server, config, log);

        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  -> " + detail)}");
            if (!ok)
            {
                failures++;
            }
        }

        await session.ConnectAsync();
        Check("连接设备", session.State == SessionState.Connected, session.StatusMessage);

        await session.MountAsync();
        var letter = session.Mount?.DriveLetter ?? '\0';
        Check("挂载盘符", session.State == SessionState.Mounted && letter != '\0', $"{letter}:");

        var driveRoot = $"{letter}:\\";
        Check("盘符可见", Directory.Exists(driveRoot), driveRoot);

        var entries = Directory.GetFileSystemEntries(driveRoot);
        Check("能枚举根目录", entries.Length > 0, $"{entries.Length} 项");

        var (rwOk, rwDetail) = RunChild($"{letter}:");
        Check("子进程读写删", rwOk, rwDetail);

        await session.UnmountAsync();
        Check("卸载后状态", session.State == SessionState.Connected, session.StatusMessage);
        Check("卸载后盘符消失", !Directory.Exists(driveRoot), driveRoot);

        await session.MountAsync();
        var letter2 = session.Mount?.DriveLetter ?? '\0';
        Check("重新挂载", session.State == SessionState.Mounted, $"{letter2}:");
        Check("重新挂载后可读", Directory.Exists($"{letter2}:\\") &&
            Directory.GetFileSystemEntries($"{letter2}:\\").Length > 0);

        await session.UnmountAsync();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "挂载冒烟全部通过" : $"{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 切换挂载根：验证「卸载 → 换根 → 重挂」不会丢盘符，且内容真的换成了新根。
    /// </summary>
    public static async Task<int> RunRootSwitchAsync(string endpoint, string root, string newRoot)
    {
        AppPaths.EnsureCreated();
        var log = new AppLog(AppPaths.LogDirectory);
        log.EntryWritten += entry =>
        {
            if (entry.Level >= LogLevel.Info)
            {
                Console.WriteLine("  " + entry.Format());
            }
        };

        var adbPath = AdbLocator.Find();
        var server = new AdbServerClient(log, adbPath);
        var config = new DeviceConfig { Endpoint = endpoint, RootPath = root };
        using var session = new DeviceSession(server, config, log);

        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  -> " + detail)}");
            if (!ok)
            {
                failures++;
            }
        }

        await session.ConnectAsync();
        Check("连接设备", session.State == SessionState.Connected, session.StatusMessage);
        Console.WriteLine($"  设备身份: {session.IdentityText} (IsRoot={session.IsRoot})");

        await session.MountAsync();
        var letter = session.Mount?.DriveLetter ?? '\0';
        Check("挂载初始根", session.State == SessionState.Mounted, $"{letter}: -> {root}");

        var before = Directory.GetFileSystemEntries($"{letter}:\\");
        Check($"枚举 {root}", before.Length > 0, $"{before.Length} 项");

        var (ok, message) = await session.ChangeRootPathAsync(newRoot);
        Check($"切换到 {newRoot}", ok, message);
        if (!ok)
        {
            await session.UnmountAsync();
            Console.WriteLine(failures == 0 ? "根路径切换全部通过" : $"{failures} 项失败");
            return failures == 0 ? 0 : 1;
        }

        Check("切换后仍是挂载态", session.State == SessionState.Mounted, session.StatusMessage);
        Check("切换后盘符没变", session.Mount?.DriveLetter == letter, $"{session.Mount?.DriveLetter}:");
        Check("切换后盘符可访问", Directory.Exists($"{letter}:\\"));

        var after = Directory.GetFileSystemEntries($"{letter}:\\");
        Check($"枚举 {newRoot}", after.Length > 0, $"{after.Length} 项");
        Check("内容确实换成了新根", !before.SequenceEqual(after), $"{before.Length} 项 -> {after.Length} 项");

        var (backOk, backMessage) = await session.ChangeRootPathAsync(root);
        Check($"切回 {root}", backOk, backMessage);
        Check("切回后盘符仍在", session.State == SessionState.Mounted && Directory.Exists($"{letter}:\\"),
            session.StatusMessage);

        await session.UnmountAsync();
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "根路径切换全部通过" : $"{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>子进程模式：在独立的进程里对挂载点做文件读写删。</summary>
    public static int RunReadWriteChild(string drive)
    {
        var probe = Path.Combine(drive + Path.DirectorySeparatorChar, $"adbdrive-rw-{DateTime.Now:HHmmssfff}.txt");
        try
        {
            File.WriteAllText(probe, "mount-smoke");
            var written = File.Exists(probe) && File.ReadAllText(probe) == "mount-smoke";
            File.Delete(probe);
            var deleted = !File.Exists(probe);
            Console.WriteLine($"ok={written} deleted={deleted} path={probe}");
            return written && deleted ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("异常: " + ex.Message);
            return 2;
        }
    }

    private static (bool Ok, string Detail) RunChild(string drive)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return (false, "拿不到自身路径");
            }

            var startInfo = new ProcessStartInfo(exe, $"--rw {drive}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "子进程启动失败");
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(30_000);
            return (process.ExitCode == 0, output.Length > 0 ? output : $"exit={process.ExitCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
