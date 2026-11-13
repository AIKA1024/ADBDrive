using Avalonia.Controls;
using Avalonia.Platform;
using ADBDrive.Views;

namespace ADBDrive.Services;

/// <summary>系统托盘图标与菜单。</summary>
public sealed class TrayService : IDisposable
{
    private TrayIcon? _trayIcon;
    private MainWindow? _window;
    private bool _disposed;

    public bool IsExiting { get; private set; }

    public event Action? ExitRequested;

    public void Attach(MainWindow window)
    {
        _window = window;
        var icon = LoadIcon();

        var menu = new NativeMenu();
        var showItem = new NativeMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);

        var hideItem = new NativeMenuItem("隐藏到托盘");
        hideItem.Click += (_, _) => HideWindow();
        menu.Items.Add(hideItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("退出 ADBDrive");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "ADBDrive · 远程 ADB 磁盘",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => ShowWindow();
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void HideWindow() => _window?.Hide();

    public void MarkExiting() => IsExiting = true;

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://ADBDrive/Assets/app-256.png"));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
