using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ADBDrive.Controls;
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

        // Ctrl+C / Ctrl+A 在隧道阶段统一处理：ListBox 会把 Ctrl+A 当条目全选吃掉，
        // 焦点也可能停在日志面板上而不是列表里，等到冒泡阶段就已经晚了。
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        if (LogList is { } logList)
        {
            // 隧道阶段接管：ListBoxItem 会先把 PointerPressed 标成 handled，
            // 而 SelectableTextBlock 自己也会选字，这里统一由我们算跨行选区。
            logList.AddHandler(PointerPressedEvent, OnLogPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            logList.AddHandler(PointerMovedEvent, OnLogPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            logList.AddHandler(PointerReleasedEvent, OnLogPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            logList.AddHandler(PointerCaptureLostEvent, OnLogPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
            logList.AddHandler(ScrollViewer.ScrollChangedEvent, OnLogScrolled, RoutingStrategies.Bubble, handledEventsToo: true);
        }
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

    // ===== 日志选择与复制 =====

    /// <summary>选区起点 / 终点：日志条目 + 该行文本内的字符偏移（用引用，滚动裁剪不会错位）。</summary>
    private (LogItemViewModel Item, int Offset)? _selAnchor;
    private (LogItemViewModel Item, int Offset)? _selCaret;
    private bool _selDragging;
    private bool _hasLogSelection;

    /// <summary>日志区的快捷键：Ctrl+C 复制选中、Ctrl+Shift+C 复制全部、Ctrl+A 全选。</summary>
    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }


        // 焦点在输入框里就完全交给它：复制 / 全选的是框里的文字，不是日志。
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.C:
                e.Handled = true;
                _ = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? CopyLogsAsync() : CopySelectedAsync();
                break;

            case Key.A:
                SelectAllLogText();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
    }

    /// <summary>日志区按下：左键开始拖选（Shift 扩展），右键弹复制菜单。</summary>
    private void OnLogPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (LogList is not { } list)
        {
            return;
        }

        var point = e.GetPosition(list);
        var props = e.GetCurrentPoint(list).Properties;

        if (props.IsRightButtonPressed)
        {
            ShowLogMenu(point);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed || HitTestLog(point, strict: true) is not { } hit)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selCaret is not null)
        {
            _selCaret = hit;
        }
        else
        {
            _selAnchor = hit;
            _selCaret = hit;
        }

        _selDragging = true;
        // ListBox 本身不可聚焦（Focusable=False），焦点要落在日志面板上，
        // 否则会一直留在搜索框里，Ctrl+C 就被那个输入框吃掉了。
        LogPanel?.Focus();
        // 抓住指针：拖出日志区（往上越过搜索栏、往下越过面板）时仍然收得到移动事件，
        // 选区不会突然断在半路，也不会让指针下面的别的控件跟着亮起来。
        e.Pointer.Capture(list);
        ApplyLogSelection();
        e.Handled = true;
    }

    private void OnLogPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_selDragging || LogList is not { } list)
        {
            return;
        }

        var point = e.GetPosition(list);
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ScrollLogIfNeeded(point);

        if (HitTestLog(point) is not { } hit || hit == _selCaret)
        {
            return;
        }

        _selCaret = hit;
        ApplyLogSelection();
        e.Handled = true;
    }

    private void OnLogPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selDragging)
        {
            e.Pointer.Capture(null);
        }

        _selDragging = false;
    }

    private void OnLogPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _selDragging = false;

    /// <summary>拖到日志区上下缘时自动滚动，否则一屏之外的内容根本选不到。</summary>
    private void ScrollLogIfNeeded(Point pointInList)
    {
        if (LogList is not { } list
            || list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
        {
            return;
        }

        var height = scroll.Viewport.Height;
        var delta = pointInList.Y < 0 ? Math.Max(pointInList.Y, -20)
            : pointInList.Y > height ? Math.Min(pointInList.Y - height, 20)
            : 0;

        if (delta == 0)
        {
            return;
        }

        var max = Math.Max(scroll.Extent.Height - height, 0);
        var target = Math.Clamp(scroll.Offset.Y + delta, 0, max);
        if (Math.Abs(target - scroll.Offset.Y) < 0.5)
        {
            return;
        }

        scroll.Offset = scroll.Offset.WithY(target);
    }

    /// <summary>滚动后新实体化的行也要补上选区颜色（拖动中由移动事件自己刷，别抢）。</summary>
    private void OnLogScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (!_selDragging && _selAnchor is not null && _selCaret is not null)
        {
            ApplyLogSelection();
        }
    }

    /// <summary>
    /// 把坐标换算成「第几行、行内第几个字符」。按下时要求真的落在某一行里（<paramref name="strict"/>）；
    /// 拖动时允许拖到行外，按纵向最近的一行吸附，横向越界则吸到行首/行尾。
    /// </summary>
    private (LogItemViewModel Item, int Offset)? HitTestLog(Point pointInList, bool strict = false)
    {
        if (LogList is not { } list || _viewModel is null)
        {
            return null;
        }

        ListBoxItem? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var item in RealizedLogItems())
        {
            var origin = item.TranslatePoint(default, list);
            if (origin is not { } topLeft)
            {
                continue;
            }

            var rect = new Rect(topLeft, item.Bounds.Size);
            var distance = pointInList.Y < rect.Top ? rect.Top - pointInList.Y
                : pointInList.Y > rect.Bottom ? pointInList.Y - rect.Bottom
                : 0;

            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = item;
            }
        }

        if (nearest is null || (strict && nearestDistance > 0))
        {
            return null;
        }

        if (nearest.DataContext is not LogItemViewModel entry)
        {
            return null;
        }

        if (nearest.GetVisualDescendants().OfType<LogLineText>().FirstOrDefault() is not { } text
            || text.TextLayout is not { } layout)
        {
            return (entry, 0);
        }

        var local = list.TranslatePoint(pointInList, text) ?? default;
        local = new Point(
            Math.Clamp(local.X, 0, Math.Max(text.Bounds.Width - 1, 0)),
            Math.Clamp(local.Y, 0, Math.Max(text.Bounds.Height - 1, 0)));

        var position = layout.HitTestPoint(local).TextPosition;
        return (entry, Math.Clamp(position, 0, text.LineLength));
    }

    private IEnumerable<ListBoxItem> RealizedLogItems() =>
        LogList is { } list
            ? list.GetRealizedContainers().OfType<ListBoxItem>()
            : [];

    /// <summary>把当前选区映射到每行文本块上（只处理已实体化的行）。</summary>
    private void ApplyLogSelection()
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var (startIndex, endIndex) = SelectedRange(out var start, out var end);
        _hasLogSelection = startIndex >= 0 && (startIndex != endIndex || start != end);

        foreach (var item in RealizedLogItems())
        {
            var text = item.GetVisualDescendants().OfType<LogLineText>().FirstOrDefault();
            if (text is null)
            {
                continue;
            }

            var row = item.DataContext is LogItemViewModel entry ? viewModel.Logs.IndexOf(entry) : -1;
            if (startIndex < 0 || row < startIndex || row > endIndex)
            {
                SetSelection(text, 0, 0);
                continue;
            }

            var length = text.LineLength;
            var from = row == startIndex ? start : 0;
            var to = row == endIndex ? end : length;
            SetSelection(text, Math.Min(from, to), Math.Max(from, to));
        }
    }

    /// <summary>只在值真变了才写：重复写会白白触发一次文本重排，拖选时看着就像在闪。</summary>
    private static void SetSelection(LogLineText text, int start, int end)
    {
        var length = text.LineLength;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, 0, length);

        if (text.SelectionStart == start && text.SelectionEnd == end)
        {
            return;
        }

        text.SelectionStart = start;
        text.SelectionEnd = end;
    }

    private void SelectAllLogText()
    {
        if (_viewModel is not { } viewModel || viewModel.Logs.Count == 0)
        {
            return;
        }

        _selAnchor = (viewModel.Logs[0], 0);
        _selCaret = (viewModel.Logs[^1], viewModel.Logs[^1].LineText.Length);
        ApplyLogSelection();
    }

    /// <summary>按选区拼出纯文本（和屏幕上高亮的字符完全一致，含时间戳与级别）。</summary>
    private (string Text, int Lines) BuildSelectedLogText()
    {
        if (_viewModel is not { } viewModel)
        {
            return (string.Empty, 0);
        }

        var (startIndex, endIndex) = SelectedRange(out var start, out var end);
        if (startIndex < 0 || (startIndex == endIndex && start == end))
        {
            return (string.Empty, 0);
        }

        var parts = new List<string>();
        for (var row = startIndex; row <= endIndex && row < viewModel.Logs.Count; row++)
        {
            var line = viewModel.Logs[row].LineText;
            var from = row == startIndex ? start : 0;
            var to = row == endIndex ? end : line.Length;
            from = Math.Clamp(from, 0, line.Length);
            to = Math.Clamp(to, 0, line.Length);
            parts.Add(from <= to ? line[from..to] : line[to..from]);
        }

        return (string.Join(Environment.NewLine, parts), parts.Count);
    }

    /// <summary>把锚点/光标折算成 [起始下标, 结束下标] 与两个字符偏移。</summary>
    private (int StartIndex, int EndIndex) SelectedRange(out int startOffset, out int endOffset)
    {
        startOffset = 0;
        endOffset = 0;
        if (_viewModel is not { } viewModel || _selAnchor is not { } anchor || _selCaret is not { } caret)
        {
            return (-1, -1);
        }

        var startItem = anchor.Item;
        var endItem = caret.Item;
        var startIndex = viewModel.Logs.IndexOf(startItem);
        var endIndex = viewModel.Logs.IndexOf(endItem);
        if (startIndex < 0 || endIndex < 0)
        {
            return (-1, -1);
        }

        if (startIndex > endIndex)
        {
            (startIndex, endIndex) = (endIndex, startIndex);
            startOffset = caret.Offset;
            endOffset = anchor.Offset;
        }
        else
        {
            startOffset = anchor.Offset;
            endOffset = caret.Offset;
        }

        return (startIndex, endIndex);
    }

    private void ShowLogMenu(Point point)
    {
        if (LogList is not { } list)
        {
            return;
        }

        var (text, lines) = BuildSelectedLogText();
        var flyout = new MenuFlyout();

        var copySelected = new MenuItem
        {
            Header = Loc.F("logs.copySelected", lines),
            IsEnabled = !string.IsNullOrEmpty(text),
        };
        copySelected.Click += async (_, _) => await CopyTextAsync(text, lines);

        var copyAll = new MenuItem { Header = Loc.T("logs.copyAll") };
        copyAll.Click += async (_, _) => await CopyLogsAsync();

        flyout.Items.Add(copySelected);
        flyout.Items.Add(copyAll);
        flyout.ShowAt(list, showAtPointer: true);
    }

    private async Task CopySelectedAsync()
    {
        var (text, lines) = BuildSelectedLogText();
        if (string.IsNullOrEmpty(text))
        {
            // 没选就退化成复制全部，省得用户还要再点一次。
            await CopyLogsAsync();
            return;
        }

        await CopyTextAsync(text, lines);
    }

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

        // 正在拖选或已经有选区时别把视图拽到底部：新日志一来面板就跳，手里的选区会跟着飞掉。
        if (_selDragging || _hasLogSelection)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_viewModel?.Logs.Count > 0)
                {
                    LogList?.ScrollIntoView(_viewModel.Logs[^1]);
                }
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
