using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GodMode.Shared.Enums;

namespace GodMode.Avalonia.Converters;

public class StateToColorConverter : IValueConverter
{
	private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4CAF50"));
	private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9800"));
	private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#9E9E9E"));
	private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#2196F3"));
	private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F44336"));
	private static readonly IBrush DefaultGray = new SolidColorBrush(Color.Parse("#757575"));

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is ProjectState state)
		{
			return state switch
			{
				ProjectState.Running => Green,
				ProjectState.WaitingInput => Orange,
				ProjectState.Stopped => Gray,
				ProjectState.Idle => Blue,
				ProjectState.Error => Red,
				_ => DefaultGray
			};
		}
		// Also handle HostState for server status display
		if (value is HostState hostState)
		{
			return hostState switch
			{
				HostState.Running => Green,
				HostState.Stopped => Gray,
				HostState.Starting => Orange,
				HostState.Stopping => Orange,
				_ => DefaultGray
			};
		}
		return DefaultGray;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
