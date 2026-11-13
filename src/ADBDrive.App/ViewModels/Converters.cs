using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ADBDrive.ViewModels;

/// <summary>XAML 里用到的轻量转换器。</summary>
public static class Converters
{
    public static readonly IValueConverter OnlineBrushConverter =
        new FuncValueConverter<bool, IBrush>(online => online ? Brush.Parse("#4ADE80") : Brush.Parse("#6E747D"));
}
