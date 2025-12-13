using GodMode.Shared.Enums;
using System.Globalization;

namespace GodMode.Maui.Converters;

/// <summary>
/// Converts HostState to visibility for the Start button
/// Show Start button when host is stopped
/// </summary>
public class StateToStartVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is HostState state)
        {
            return state == HostState.Stopped || state == HostState.Unknown;
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts HostState to visibility for the Stop button
/// Show Stop button when host is running
/// </summary>
public class StateToStopVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is HostState state)
        {
            return state == HostState.Running;
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
