using System.Text;

namespace ADBDrive.Core.Adb;

/// <summary>
/// Windows 挂载路径（\DCIM\Camera）与 Android 设备路径（/sdcard/DCIM/Camera）之间的转换，
/// 以及 shell 命令的安全引用。
/// </summary>
public sealed class DevicePathMapper
{
    public DevicePathMapper(string deviceRoot)
    {
        var root = string.IsNullOrWhiteSpace(deviceRoot) ? "/" : deviceRoot.Replace('\\', '/');
        if (!root.StartsWith('/'))
        {
            root = "/" + root;
        }

        Root = root.Length > 1 ? root.TrimEnd('/') : root;
    }

    /// <summary>设备侧的根路径，例如 /sdcard。</summary>
    public string Root { get; }

    public bool IsRootPath(string windowsPath)
    {
        var rel = Normalize(windowsPath);
        return rel.Length == 0;
    }

    /// <summary>把 Windows 相对路径转换成设备绝对路径。</summary>
    public string ToDevicePath(string windowsPath)
    {
        var rel = Normalize(windowsPath);
        if (rel.Length == 0)
        {
            return Root;
        }

        return Root.Length == 1 && Root[0] == '/' ? "/" + rel : Root + "/" + rel;
    }

    /// <summary>把设备路径转回 Windows 相对路径（仅用于日志/调试）。</summary>
    public string ToWindowsPath(string devicePath)
    {
        if (devicePath.StartsWith(Root, StringComparison.Ordinal))
        {
            var rel = devicePath[Root.Length..].Trim('/');
            return rel.Length == 0 ? "\\" : "\\" + rel.Replace('/', '\\');
        }

        return "\\" + devicePath.Trim('/').Replace('/', '\\');
    }

    /// <summary>规范化相对路径：统一分隔符、去掉首尾分隔符、拒绝 .. 和非法字符。</summary>
    public static string Normalize(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath))
        {
            return string.Empty;
        }

        var replaced = windowsPath.Replace('\\', '/');
        var segments = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(replaced.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new AdbException(AdbErrorKind.IoError, "路径中不允许出现 ..");
            }

            if (segment.IndexOf('\0') >= 0)
            {
                throw new AdbException(AdbErrorKind.IoError, "路径中包含非法字符");
            }

            if (builder.Length > 0)
            {
                builder.Append('/');
            }

            builder.Append(segment);
        }

        return builder.ToString();
    }

    /// <summary>把任意字符串安全地放进 sh 命令行（单引号包裹，内部单引号转义）。</summary>
    public static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\\''") + "'";
    }
}

/// <summary>Windows 通配符匹配（* 和 ?），大小写不敏感。</summary>
public static class WildcardMatcher
{
    public static bool IsMatch(string pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "*.*")
        {
            return true;
        }

        return Match(pattern.AsSpan(), name.AsSpan());
    }

    private static bool Match(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name)
    {
        // 经典的双指针回溯实现，避免递归爆栈。
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n])))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
