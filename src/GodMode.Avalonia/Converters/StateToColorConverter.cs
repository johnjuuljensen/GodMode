using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GodMode.Shared.Enums;

namespace GodMode.Avalonia.Converters;

public class StateToColorConverter : IValueConverter
{
	private static readonly IBrush Running = new SolidColorBrush(Color.Parse("#30D158"));
	private static readonly IBrush Waiting = new SolidColorBrush(Color.Parse("#FFD60A"));
	private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#FF453A"));
	private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#8CFFFFFF"));
	private static readonly IBrush Stopped = new SolidColorBrush(Color.Parse("#59FFFFFF"));
	private static readonly IBrush DefaultGray = new SolidColorBrush(Color.Parse("#59FFFFFF"));

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is ProjectState state)
		{
			return state switch
			{
				ProjectState.Running => Running,
				ProjectState.WaitingInput => Waiting,
				ProjectState.Stopped => Stopped,
				ProjectState.Idle => Idle,
				ProjectState.Error => Error,
				_ => DefaultGray
			};
		}
		if (value is ServerState hostState)
		{
			return hostState switch
			{
				ServerState.Running => Running,
				ServerState.Stopped => Stopped,
				ServerState.Starting => Waiting,
				ServerState.Stopping => Waiting,
				_ => DefaultGray
			};
		}
		return DefaultGray;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
