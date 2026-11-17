using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ADBDrive.Services;
using ADBDrive.ViewModels;
using ADBDrive.Views;

namespace ADBDrive;

public partial class App : Application
{
    private static bool _cleanedUp;

    public static AppHost Host { get; private set; } = null!;

    public static TrayService Tray { get; private set; } = null!;

    public static bool IsExiting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Host = new AppHost();
        Tray = new TrayService(Host.Log);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel(Host);
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => Cleanup();

            Tray.Attach(window);
            Tray.ExitRequested += () => Exit(desktop);

            // 第二个实例启动时只发信号；这里在 UI 线程上把已运行的窗口显示并置前。
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(() =>
            {
                if (!IsExiting)
                {
                    Tray.ShowWindow();
                }
            }));

            window.Opened += async (_, _) =>
            {
                if (Host.Config.StartMinimized)
                {
                    window.Hide();
                }

                await Host.InitializeAsync();
                await viewModel.RefreshCommand.ExecuteAsync(null);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void Exit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        IsExiting = true;
        Cleanup();
        desktop.Shutdown();
    }

    private static void Cleanup()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        try
        {
            Tray?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            Host?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
