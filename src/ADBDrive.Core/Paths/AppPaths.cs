namespace ADBDrive.Core.Paths;

/// <summary>应用的本地目录约定。</summary>
public static class AppPaths
{
    public const string ProductName = "ADBDrive";

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductName);

    public static string ConfigDirectory => Path.Combine(DataDirectory, "config");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}

/// <summary>定位本机 adb.exe。</summary>
public static class AdbLocator
{
    public static string? Find(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var candidates = new List<string>();
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(directory.Trim(), "adb.exe"));
            }
            catch
            {
                // 忽略非法 PATH 项
            }
        }

        var appDirectory = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(appDirectory, "adb.exe"));
        candidates.Add(Path.Combine(appDirectory, "platform-tools", "adb.exe"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(userProfile, ".local", "bin", "adb.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // 忽略无法访问的路径
            }
        }

        return null;
    }
}
