using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ADBDrive.Services;

/// <summary>
/// Windows 窗口置前辅助：恢复最小化窗口并绕过前台锁定把它拉到前台。
/// 单实例唤醒与托盘「显示主窗口」共用，行为一致。
/// </summary>
internal static class WindowActivation
{
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static void BringToFront(Window? window)
    {
        if (window is null)
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ShowWindow(handle, SW_RESTORE);

            // Windows 只允许前台进程抢焦点：临时把本线程挂到当前前台线程上再置前。
            var currentThread = GetCurrentThreadId();
            var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            var attached = foregroundThread != 0
                && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                BringWindowToTop(handle);
                SetForegroundWindow(handle);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }
        catch
        {
            // 非 Windows 或平台不支持时忽略；Avalonia 的 Activate() 已先尝试过。
        }
    }
}
