using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace ADBDrive;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        if (!AcquireSingleInstance())
        {
            MessageBoxW(IntPtr.Zero, "ADBDrive 已经在运行，请从系统托盘打开窗口。", "ADBDrive", 0x40);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // AOT 下没有调试器，启动期异常至少留下痕迹。
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ADBDrive", "logs", "crash.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.AppendAllText(path, $"{DateTime.Now:O} {ex}{Environment.NewLine}");
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>托盘程序只允许一个实例，否则会出现多个图标并争抢同一盘符。</summary>
    private static bool AcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\ADBDrive.SingleInstance", out var createdNew);
            return createdNew;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
