using GodMode.Shared.Enums;
using System.Globalization;

namespace GodMode.Maui.Converters;

/// <summary>
/// Converts ProjectState to background color for state badge.
/// Uses colors that work well with white text.
/// </summary>
public class StateToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ProjectState state)
        {
            return state switch
            {
                ProjectState.Running => Color.FromArgb("#4CAF50"),      // Green
                ProjectState.WaitingInput => Color.FromArgb("#FF9800"), // Orange
                ProjectState.Stopped => Color.FromArgb("#9E9E9E"),      // Gray
                ProjectState.Idle => Color.FromArgb("#2196F3"),         // Blue
                ProjectState.Error => Color.FromArgb("#F44336"),        // Red
                _ => Color.FromArgb("#757575")                          // Default gray
            };
        }
        return Color.FromArgb("#757575");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
