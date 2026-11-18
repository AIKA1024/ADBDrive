using Microsoft.Win32;

namespace ADBDrive.Services;

/// <summary>
/// 开机自启：注册到当前用户的 HKCU Run 键，不需要管理员权限。
/// 注册表是唯一状态来源——读取时直接看键值，不依赖 config，避免用户手动改启动项后界面显示错误。
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ADBDrive";
    private const string StartupArg = "--startup";

    /// <summary>命令行是否带 --startup（自启传入，用于强制最小化到托盘）。</summary>
    public static bool IsStartupLaunch(string[] args) =>
        args.Any(a => string.Equals(a, StartupArg, StringComparison.OrdinalIgnoreCase));

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置开机自启；返回是否成功，失败时带错误说明。</summary>
    public static (bool Success, string? Error) SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return (false, "无法打开启动项注册表键");
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    return (false, "无法确定程序路径");
                }

                // 加引号防止路径里有空格；--startup 让自启时直接进托盘。
                key.SetValue(ValueName, $"\"{exePath}\" {StartupArg}", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
