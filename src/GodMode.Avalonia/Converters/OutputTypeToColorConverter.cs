using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GodMode.Avalonia.Converters;

public class MessageTypeToColorConverter : IValueConverter
{
	private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#2196F3"));
	private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4CAF50"));
	private static readonly IBrush Teal = new SolidColorBrush(Color.Parse("#009688"));
	private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F44336"));
	private static readonly IBrush Purple = new SolidColorBrush(Color.Parse("#9C27B0"));
	private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#757575"));

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is string type)
		{
			return type.ToLowerInvariant() switch
			{
				"user" => Blue,
				"assistant" => Green,
				"result" => Teal,
				"error" => Red,
				"system" => Purple,
				_ => Gray
			};
		}
		return Gray;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class ContentTypeToColorConverter : IValueConverter
{
	private static readonly IBrush BlueGray = new SolidColorBrush(Color.Parse("#607D8B"));
	private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9800"));
	private static readonly IBrush Brown = new SolidColorBrush(Color.Parse("#795548"));
	private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#757575"));

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is string type)
		{
			return type.ToLowerInvariant() switch
			{
				"text" => BlueGray,
				"tool_use" => Orange,
				"tool_result" => Brown,
				_ => Gray
			};
		}
		return Gray;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
