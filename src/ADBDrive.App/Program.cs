using Avalonia;
using ADBDrive.Services;

namespace ADBDrive;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!SingleInstance.Acquire())
        {
            // 已有一个实例在运行：通知它把窗口拉到前台，然后安静退出。
            // 不再弹 MessageBox 让用户自己去托盘里找。
            SingleInstance.NotifyExistingInstance();
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
}
