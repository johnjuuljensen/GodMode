using GodMode.Shared.Enums;
using System.Globalization;

namespace GodMode.Maui.Converters;

public class StateToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ProjectState state)
        {
            return state switch
            {
                ProjectState.Running => Colors.Blue,
                ProjectState.WaitingInput => Colors.Orange,
                ProjectState.Error => Colors.Red,
                ProjectState.Stopped => Colors.Gray,
                ProjectState.Idle => Colors.Green,
                _ => Colors.Gray
            };
        }
        return Colors.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
