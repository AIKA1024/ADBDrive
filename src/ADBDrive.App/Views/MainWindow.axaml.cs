using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
