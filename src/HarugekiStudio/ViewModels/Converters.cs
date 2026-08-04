using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace HarugekiStudio.ViewModels;

public sealed class HighlightBrushConverter : IValueConverter
{
    public IBrush? TrueBrush { get; set; } = new SolidColorBrush(Color.Parse("#FFD700"));
    public IBrush? FalseBrush { get; set; } = new SolidColorBrush(Color.Parse("#EEEEEE"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b ? TrueBrush : FalseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class HighlightWeightConverter : IValueConverter
{
    public FontWeight TrueWeight { get; set; } = FontWeight.Bold;
    public FontWeight FalseWeight { get; set; } = FontWeight.Normal;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b ? TrueWeight : FalseWeight;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
