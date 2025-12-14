using GodMode.Shared.Enums;
using System.Globalization;

namespace GodMode.Maui.Converters;

public class OutputTypeToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is OutputEventType type)
        {
            return type switch
            {
                OutputEventType.User => Color.FromArgb("#E3F2FD"),
                OutputEventType.Assistant => Color.FromArgb("#F5F5F5"),
                OutputEventType.Result => Color.FromArgb("#E0F2F1"),
                OutputEventType.Error => Color.FromArgb("#FFEBEE"),
                OutputEventType.System => Color.FromArgb("#F3E5F5"),
                _ => Colors.White
            };
        }
        return Colors.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
