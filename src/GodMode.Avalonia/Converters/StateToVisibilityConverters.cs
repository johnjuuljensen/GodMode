using System.Globalization;
using Avalonia.Data.Converters;
using GodMode.Shared.Enums;

namespace GodMode.Avalonia.Converters;

public class ProjectStateToWaitingVisibilityConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is ProjectState state)
			return state == ProjectState.WaitingInput;
		return false;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class StateToStartVisibilityConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is ServerState state)
			return state is ServerState.Stopped or ServerState.Unknown;
		return false;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class StateToStopVisibilityConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is ServerState state)
			return state == ServerState.Running;
		return false;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
