using System.Globalization;
using Avalonia.Data.Converters;

namespace GodMode.Avalonia.Converters;

public class RelativeTimeConverter : IValueConverter
{
	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not DateTime dateTime) return "";

		var now = DateTime.Now;
		var utcNow = DateTime.UtcNow;

		// Handle both UTC and local DateTimes
		var diff = dateTime.Kind == DateTimeKind.Utc
			? utcNow - dateTime
			: now - dateTime;

		if (diff.TotalSeconds < 0) return "just now";

		return diff.TotalSeconds switch
		{
			< 30 => "just now",
			< 60 => $"{(int)diff.TotalSeconds}s ago",
			< 3600 => $"{(int)diff.TotalMinutes}m ago",
			< 86400 => $"{(int)diff.TotalHours}h ago",
			< 172800 => "yesterday",
			_ => $"{(int)diff.TotalDays}d ago"
		};
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotSupportedException();
}
