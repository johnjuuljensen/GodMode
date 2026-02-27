using System.Globalization;
using Avalonia.Data.Converters;
using GodMode.Shared.Enums;

namespace GodMode.Avalonia.Converters;

public class StateToStartVisibilityConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is HostState state)
			return state is HostState.Stopped or HostState.Unknown;
		return false;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class StateToStopVisibilityConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is HostState state)
			return state == HostState.Running;
		return false;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
