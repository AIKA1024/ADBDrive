using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ADBDrive.Controls;

/// <summary>
/// 一行日志的文本面：时间、级别、消息是同一个可选中的文本（Avalonia 12 的 SelectableTextBlock 支持 Inlines），
/// 所以从时间戳中间开始拖选也能整段选中、复制不缺字。级别用 <see cref="LevelStart"/>/<see cref="LevelLength"/>
/// 定位后单独着色，字符下标与 <see cref="LineText"/> 完全一一对应。
/// </summary>
public sealed class LogLineText : SelectableTextBlock
{
    /// <summary>时间戳的灰色，级别和消息各用各自的前景色。</summary>
    private static readonly IBrush MutedBrush = Brush.Parse("#5A616B");

    public static readonly StyledProperty<string?> LineTextProperty =
        AvaloniaProperty.Register<LogLineText, string?>(nameof(LineText));

    public static readonly StyledProperty<int> LevelStartProperty =
        AvaloniaProperty.Register<LogLineText, int>(nameof(LevelStart));

    public static readonly StyledProperty<int> LevelLengthProperty =
        AvaloniaProperty.Register<LogLineText, int>(nameof(LevelLength));

    public static readonly StyledProperty<IBrush?> LevelBrushProperty =
        AvaloniaProperty.Register<LogLineText, IBrush?>(nameof(LevelBrush));

    /// <summary>整行文本（时间 + 级别 + 消息），选区偏移以它为准。</summary>
    public string? LineText
    {
        get => GetValue(LineTextProperty);
        set => SetValue(LineTextProperty, value);
    }

    /// <summary>级别两个字在 <see cref="LineText"/> 中的起始下标。</summary>
    public int LevelStart
    {
        get => GetValue(LevelStartProperty);
        set => SetValue(LevelStartProperty, value);
    }

    /// <summary>级别的字符数。</summary>
    public int LevelLength
    {
        get => GetValue(LevelLengthProperty);
        set => SetValue(LevelLengthProperty, value);
    }

    public IBrush? LevelBrush
    {
        get => GetValue(LevelBrushProperty);
        set => SetValue(LevelBrushProperty, value);
    }

    /// <summary>整行字符数。</summary>
    public int LineLength => LineText?.Length ?? 0;

    // 上一次真正渲染的内容：属性是逐个绑上来的，同一个容器会连续触发好几次，没必要重建。
    private string? _renderedText;
    private int _renderedStart;
    private int _renderedLength;
    private IBrush? _renderedBrush;

    /// <summary>沿用 SelectableTextBlock 的 ControlTheme（IBeam 光标、默认选区色）。</summary>
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LineTextProperty
            || change.Property == LevelStartProperty
            || change.Property == LevelLengthProperty
            || change.Property == LevelBrushProperty)
        {
            RebuildInlines();
        }
    }

    /// <summary>把整行切成「时间」「级别」「消息」三段，只有级别那段换成自己的颜色。</summary>
    private void RebuildInlines()
    {
        var text = LineText ?? string.Empty;
        var start = Math.Clamp(LevelStart, 0, text.Length);
        var end = Math.Clamp(start + Math.Max(LevelLength, 0), start, text.Length);

        if (text == _renderedText
            && start == _renderedStart
            && (end - start) == _renderedLength
            && ReferenceEquals(LevelBrush, _renderedBrush))
        {
            return;
        }

        var contentChanged = text != _renderedText;
        _renderedText = text;
        _renderedStart = start;
        _renderedLength = end - start;
        _renderedBrush = LevelBrush;

        // 容器被复用给另一条日志时，旧选区不能跟着带过去（否则滚动/拖选时会闪出错误的蓝块）。
        if (contentChanged)
        {
            SelectionStart = 0;
            SelectionEnd = 0;
        }

        var inlines = new InlineCollection();

        if (start > 0)
        {
            inlines.Add(new Run(text[..start]) { Foreground = MutedBrush });
        }

        if (end > start)
        {
            var level = new Run(text[start..end]);
            if (LevelBrush is { } brush)
            {
                level.Foreground = brush;
            }

            inlines.Add(level);
        }

        if (end < text.Length)
        {
            inlines.Add(new Run(text[end..]));
        }

        Inlines = inlines;
    }
}
