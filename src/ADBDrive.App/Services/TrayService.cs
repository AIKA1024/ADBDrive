using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using ADBDrive.Core.Diagnostics;
using ADBDrive.Views;

namespace ADBDrive.Services;

/// <summary>系统托盘图标与菜单。</summary>
public sealed class TrayService : IDisposable
{
    private readonly AppLog? _log;
    private TrayIcon? _trayIcon;
    private TrayIcons? _icons;
    private MainWindow? _window;
    private bool _disposed;

    public TrayService(AppLog? log = null) => _log = log;

    public bool IsExiting { get; private set; }

    public event Action? ExitRequested;

    public void Attach(MainWindow window)
    {
        _window = window;

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
            Icon = LoadIcon(),
            ToolTipText = "ADBDrive · 远程 ADB 磁盘",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowWindow();

        // Avalonia 12 起 TrayIcon 只是普通对象：必须注册到 Application 的附加属性
        // TrayIcon.Icons 上，平台层才会真正创建托盘图标（Win32 下是 Shell_NotifyIcon）。
        // 只 new 出来再设 IsVisible 是**静默无效**的 —— 本程序踩过这个坑。
        var app = Application.Current;
        if (app is null)
        {
            _log?.Warn("托盘", "Application.Current 为空，无法注册托盘图标");
            return;
        }

        _icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(app, _icons);

        // 注册后 Attach() 会读取上述属性并创建平台图标，此时再显式置可见。
        _trayIcon.IsVisible = true;

        if (_trayIcon.NativeMenuExporter is null)
        {
            _log?.Warn("托盘", "托盘图标未在平台层注册成功（NativeMenuExporter 为空）");
        }
        else
        {
            _log?.Info("托盘", $"托盘图标已注册：{_trayIcon.ToolTipText}");
        }
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

        try
        {
            _trayIcon?.Dispose();
        }
        catch
        {
            // 退出路径，忽略
        }

        _trayIcon = null;

        if (_icons is not null && Application.Current is { } app)
        {
            try
            {
                TrayIcon.SetIcons(app, null);
            }
            catch
            {
                // 退出路径，忽略
            }
        }

        _icons = null;
    }
}
