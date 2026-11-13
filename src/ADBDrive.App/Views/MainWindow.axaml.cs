using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ADBDrive.ViewModels;

namespace ADBDrive.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
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
