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
                OutputEventType.UserInput => Color.FromArgb("#E3F2FD"),
                OutputEventType.AssistantOutput => Color.FromArgb("#F5F5F5"),
                OutputEventType.Thinking => Color.FromArgb("#FFF3E0"),
                OutputEventType.ToolUse => Color.FromArgb("#E8F5E9"),
                OutputEventType.ToolResult => Color.FromArgb("#E0F2F1"),
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
