using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DClean.UI.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is Visibility.Visible;
}

[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is false;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is false;
}

[ValueConversion(typeof(long), typeof(string))]
public sealed class BytesFormatConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not long bytes) return "0 B";
        return bytes switch
        {
            < 1024               => $"{bytes} B",
            < 1_048_576          => $"{bytes / 1024.0:F1} KB",
            < 1_073_741_824      => $"{bytes / 1_048_576.0:F1} MB",
            _                    => $"{bytes / 1_073_741_824.0:F2} GB",
        };
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(double), typeof(string))]
public sealed class PercentFormatConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is double d ? $"{d:F0}%" : "0%";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is Visibility.Collapsed;
}

[ValueConversion(typeof(int), typeof(bool))]
public sealed class IntToBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int i && i > 0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(string), typeof(Visibility))]
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush  { get; set; } = Brushes.LimeGreen;
    public Brush FalseBrush { get; set; } = Brushes.OrangeRed;

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? TrueBrush : FalseBrush;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(int), typeof(string))]
public sealed class HealthScoreToLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int score ? score switch
        {
            >= 90 => "מצוין",
            >= 75 => "טוב",
            >= 50 => "דורש בדיקה",
            _ => "בסיכון"
        } : "לא ידוע";

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(int), typeof(Brush))]
public sealed class HealthScoreToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        string color = value is int score ? score switch
        {
            >= 90 => "#107C10",
            >= 75 => "#00D2B8",
            >= 50 => "#FFB900",
            _ => "#D13438"
        } : "#5A6478";

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(int), typeof(Brush))]
public sealed class TemperatureToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        string color = value is int temp ? temp switch
        {
            >= 60 => "#D13438",
            >= 50 => "#FFB900",
            _ => "#00D2B8"
        } : "#5A6478";

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
