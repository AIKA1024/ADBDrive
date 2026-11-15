using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ADBDrive.Core.Localization;
using ADBDrive.ViewModels;

namespace ADBDrive.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Loc.Changed += UpdateMaximizeGlyph;
        Closed += (_, _) => Loc.Changed -= UpdateMaximizeGlyph;
    }

    // ===== 自定义标题栏 =====

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && MaxGlyph is not null)
        {
            UpdateMaximizeGlyph();
        }
    }

    // 最大化 = 单个方框；还原 = 两个错开的方框（与系统字形一致）
    private const string MaximizeGlyph = "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z";
    private const string RestoreGlyph = "M3.5,0.5 L9.5,0.5 L9.5,6.5 M0.5,3.5 L6.5,3.5 L6.5,9.5 L0.5,9.5 Z";

    private void UpdateMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxGlyph.Data = Geometry.Parse(maximized ? RestoreGlyph : MaximizeGlyph);
        if (MaxButton is not null)
        {
            ToolTip.SetTip(MaxButton, Loc.T(maximized ? "caption.restore" : "caption.maximize"));
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ===== 设备行 =====

    /// <summary>
    /// 行尾的「⋯」打开和设备右键完全相同的菜单 —— 菜单本身挂在 Border 上，
    /// 这样两处共用一份实例，也保证 DataContext 来自设备行。
    /// </summary>
    /// <summary>设备行左键 = 展开 / 收起这一行的设备设置（⋯ 里仍是完整操作菜单）。</summary>
    private void OnDeviceRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border row || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsInsideRowWidget(e.Source as Visual, row) || row.DataContext is not DeviceViewModel device)
        {
            return;
        }

        device.ToggleSettingsCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// 只有点在「行的空白/文字」上才切换设置：按钮、输入框、以及已经展开的设置面板
    /// 都要放行，否则点输入框想改内容时会顺手把面板收起来。
    /// </summary>
    private static bool IsInsideRowWidget(Visual? source, Visual row)
    {
        for (var visual = source; visual is not null && visual != row; visual = visual.GetVisualParent())
        {
            if (visual is Button or TextBox or ComboBox or Control { Name: "SettingsArea" })
            {
                return true;
            }
        }

        return false;
    }



    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            return;
        }

        // 最大化状态下拖动：先还原，并按光标在标题栏里的相对位置重新定位，避免窗口跳走。
        if (WindowState == WindowState.Maximized)
        {
            var cursorInWindow = e.GetPosition(this);
            var cursorOnScreen = this.PointToScreen(cursorInWindow);
            var ratio = cursorInWindow.X / Math.Max(1, Bounds.Width);

            WindowState = WindowState.Normal;

            var restoredWidth = Bounds.Width;
            Position = new PixelPoint(
                (int)(cursorOnScreen.X - (restoredWidth * ratio)),
                (int)(cursorOnScreen.Y - cursorInWindow.Y));
        }

        BeginMoveDrag(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.LogAdded -= OnLogAdded;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LogAdded += OnLogAdded;
        }
    }

    // ===== 日志复制 =====

    private async void OnCopyLogLineClick(object? sender, RoutedEventArgs e)
    {
        // Flyout 的 DataContext 继承自所属的日志行。
        if (sender is MenuItem { DataContext: LogItemViewModel item })
        {
            await CopyTextAsync(item.ToLine(), 1);
        }
    }

    private async void OnCopyLogsClick(object? sender, RoutedEventArgs e) => await CopyLogsAsync();

    private async Task CopyLogsAsync()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var text = viewModel.BuildLogText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await CopyTextAsync(text, viewModel.Logs.Count);
    }

    private async Task CopyTextAsync(string text, int lines)
    {
        if (Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            _viewModel?.ShowCopiedHint(lines);
        }
        catch (Exception ex)
        {
            App.Host.Log.Warn("界面", $"复制到剪贴板失败: {ex.Message}");
        }
    }

    private void OnLogAdded()
    {
        if (_viewModel?.IsLogVisible != true)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                LogScroll?.ScrollToEnd();
            }
            catch
            {
                // 窗口还没完成布局时忽略。
            }
        }, DispatcherPriority.Background);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (App.IsExiting)
        {
            return;
        }

        var minimize = App.Host.Config.MinimizeToTray;
        if (minimize)
        {
            // 关窗口只是收到托盘，避免用户误关后进程还在但看不到。
            e.Cancel = true;
            Hide();
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            e.Cancel = true;
            App.Exit(desktop);
        }
    }
}
