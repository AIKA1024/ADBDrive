using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADBDrive.Core.Config;

/// <summary>一台保存下来的设备配置。</summary>
public sealed class DeviceConfig
{
    /// <summary>adb 设备标识：TCP 设备为 host:port，USB 设备为序列号。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>用户自定义名称（为空时用手机型号）。</summary>
    public string? DisplayName { get; set; }

    /// <summary>挂载的设备根目录。</summary>
    public string RootPath { get; set; } = "/sdcard";

    /// <summary>首选盘符（空表示自动分配）。</summary>
    public string? DriveLetter { get; set; }

    /// <summary>启动时自动连接。</summary>
    public bool AutoConnect { get; set; }

    /// <summary>连接成功后自动挂载。</summary>
    public bool AutoMount { get; set; }

    /// <summary>是否启用目录 / 属性缓存。</summary>
    public bool UseCache { get; set; } = true;

    public DeviceConfig Clone() => new()
    {
        Endpoint = Endpoint,
        DisplayName = DisplayName,
        RootPath = RootPath,
        DriveLetter = DriveLetter,
        AutoConnect = AutoConnect,
        AutoMount = AutoMount,
        UseCache = UseCache,
    };
}

/// <summary>应用配置。</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public string? AdbPath { get; set; }

    public bool StartMinimized { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>界面语言："system"（跟随系统）/ "zh-Hans" / "en"。null 等同 system。</summary>
    public string? Language { get; set; }

    /// <summary>目录缓存秒数。</summary>
    public int CacheSeconds { get; set; } = 3;

    public List<DeviceConfig> Devices { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppConfig))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}

/// <summary>配置读写（JSON，源生成序列化，AOT 安全）。</summary>
public static class ConfigStore
{
    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppConfig();
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppConfig();
            }

            return JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(string path, AppConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
