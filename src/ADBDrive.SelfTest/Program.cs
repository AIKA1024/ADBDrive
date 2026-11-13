using System.Diagnostics;
using System.Security.Cryptography;
using ADBDrive.Core.Adb;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Core.FileSystem;
using ADBDrive.Core.Paths;
using ADBDrive.SelfTest;

var endpoint = args.Length > 0 ? args[0] : "127.0.0.1:5555";
var root = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "/sdcard";

if (args.Contains("--rw"))
{
    return MountSmoke.RunReadWriteChild(args.Last());
}

if (args.Contains("--mount"))
{
    return await MountSmoke.RunAsync(endpoint, root);
}

if (args.Contains("--switch"))
{
    var index = Array.IndexOf(args, "--switch");
    var target = index + 1 < args.Length ? args[index + 1] : "/data";
    return await MountSmoke.RunRootSwitchAsync(endpoint, root, target);
}

if (args.Contains("--delete"))
{
    var target = args.Last();
    Console.WriteLine($"deleting {target}, exists={File.Exists(target)}");
    try
    {
        File.Delete(target);
        Console.WriteLine($"after delete exists={File.Exists(target)}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("delete failed: " + ex.Message);
        return 1;
    }
}

AppPaths.EnsureCreated();
var log = new AppLog(AppPaths.LogDirectory);
log.EntryWritten += entry => Console.WriteLine("  " + entry.Format());

var adbPath = AdbLocator.Find();
Console.WriteLine($"adb: {adbPath}");
var server = new AdbServerClient(log, adbPath);
var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail is null ? "" : "  -> " + detail)}");
    if (!ok)
    {
        failures++;
    }
}

try
{
    var running = await server.EnsureServerAsync(CancellationToken.None);
    Check("adb server 可用", running, server.Endpoint);

    var devices = await server.ListDevicesAsync(CancellationToken.None);
    Check("设备列表非空", devices.Count > 0, string.Join(", ", devices.Select(d => $"{d.Serial}={d.State}")));

    var connect = await server.ConnectAsync(endpoint, CancellationToken.None);
    Check($"connect {endpoint}", connect.Success, connect.Message);

    devices = await server.ListDevicesAsync(CancellationToken.None);
    var info = devices.FirstOrDefault(d => d.Serial == endpoint)
               ?? throw new InvalidOperationException($"设备列表中没有 {endpoint}");
    Check("设备在线", info.IsOnline, info.DisplayName);

    var device = new AdbDeviceConnection(server, info, log);
    await device.LoadFeaturesAsync(CancellationToken.None);
    Check("读到 features", device.SupportsSyncV2 || !device.SupportsSyncV2, $"sync_v2={device.SupportsSyncV2}");

    var mapper = new DevicePathMapper(root);
    var fs = new AdbFileSystem(device, mapper, log);

    var rootEntry = await fs.StatAsync(root, CancellationToken.None);
    Check("stat 根目录", rootEntry is { IsDirectory: true }, rootEntry?.ToString());

    var entries = await fs.ListAsync(root, CancellationToken.None);
    Check("列目录", entries.Count > 0, $"{entries.Count} 项, 前几项: {string.Join(", ", entries.Take(5).Select(e => e.Name))}");

    var space = await fs.GetDiskSpaceAsync(CancellationToken.None);
    Check("磁盘空间", space.TotalBytes > 0, $"free={space.FreeBytes / 1024 / 1024}MB total={space.TotalBytes / 1024 / 1024}MB");

    // ---- 写入 / 读回 / 校验 ----
    var remoteDir = $"{root.TrimEnd('/')}/ADBDriveTest";
    await fs.CreateDirectoryAsync(remoteDir, CancellationToken.None);
    Check("创建目录", (await fs.StatAsync(remoteDir, CancellationToken.None))?.IsDirectory == true);

    var payload = RandomNumberGenerator.GetBytes(1024 * 1024 * 3 + 12345);
    var expectedHash = Convert.ToHexString(SHA256.HashData(payload));
    var remoteFile = $"{remoteDir}/upload.bin";

    var sw = Stopwatch.StartNew();
    using (var input = new MemoryStream(payload))
    {
        await fs.PushAsync(remoteFile, input, CancellationToken.None);
    }
    sw.Stop();
    Check("push 3MB", true, $"{sw.ElapsedMilliseconds}ms ({payload.Length / 1024.0 / Math.Max(1, sw.ElapsedMilliseconds) * 1000:F0} KB/s)");

    var stat = await fs.StatAsync(remoteFile, CancellationToken.None);
    Check("push 后大小正确", stat?.Size == payload.Length, $"{stat?.Size} vs {payload.Length}");

    sw.Restart();
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var stream = await fs.OpenReadAsync(remoteFile, payload.Length, CancellationToken.None);
    var buffer = new byte[128 * 1024];
    long totalRead = 0;
    while (true)
    {
        var read = await stream.ReadAsync(buffer, CancellationToken.None);
        if (read <= 0) break;
        hash.AppendData(buffer, 0, read);
        totalRead += read;
    }
    await stream.DisposeAsync();
    sw.Stop();
    var actualHash = Convert.ToHexString(hash.GetHashAndReset());
    Check("流式读取内容一致", actualHash == expectedHash && totalRead == payload.Length,
        $"{totalRead} bytes, {sw.ElapsedMilliseconds}ms");
    Check("读带宽", true, $"{totalRead / 1024.0 / Math.Max(1, sw.ElapsedMilliseconds) * 1000:F0} KB/s");

    // ---- 随机位置读取（模拟 Explorer 分段读） ----
    await using (var sync = await device.OpenSyncAsync(CancellationToken.None))
    {
        var st = await sync.StatAsync(remoteFile, CancellationToken.None);
        Check("sync 直接 stat", st?.Size == payload.Length, st?.ToString());
    }

    // ---- 重命名 ----
    var renamed = $"{remoteDir}/renamed.bin";
    await fs.MoveAsync(remoteFile, renamed, replace: true, CancellationToken.None);
    Check("重命名", (await fs.StatAsync(renamed, CancellationToken.None))?.Size == payload.Length);

    // ---- 修改时间 ----
    var target = DateTimeOffset.UtcNow.AddHours(-3);
    await fs.SetLastWriteTimeAsync(renamed, target, CancellationToken.None);
    var updated = await fs.StatAsync(renamed, CancellationToken.None, bypassCache: true);
    Check("设置修改时间", updated is not null && Math.Abs((updated.Value.Modified - target).TotalSeconds) <= 2,
        $"{updated?.Modified:O} vs {target:O}");

    // ---- 权限拒绝 / 不存在 ----
    var uid = (await device.ShellAsync("id -u", CancellationToken.None)).StandardOutput.Trim();
    if (uid == "0")
    {
        Console.WriteLine("SKIP  无权限目录报错（设备已是 root，无法构造该场景）");
    }
    else
    {
        try
        {
            await fs.ListAsync("/data/system", CancellationToken.None);
            Check("无权限目录应报错", false, "居然成功了");
        }
        catch (AdbException ex)
        {
            Check("无权限目录报错", ex.Kind == AdbErrorKind.PermissionDenied, ex.Kind + ": " + ex.Message);
        }
    }

    var missing = await fs.StatAsync($"{root.TrimEnd('/')}/不存在的东西", CancellationToken.None);
    Check("不存在的路径返回 null", missing is null);

    // ---- 清理 ----
    await fs.DeleteFileAsync(renamed, CancellationToken.None);
    await fs.DeleteDirectoryAsync(remoteDir, CancellationToken.None);
    Check("清理", (await fs.StatAsync(remoteDir, CancellationToken.None)) is null);
}
catch (Exception ex)
{
    Console.WriteLine("EXCEPTION: " + ex);
    failures++;
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
return failures == 0 ? 0 : 1;
