using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using GodMode.Shared.Models;

namespace GodMode.Avalonia.Converters;

public class MessageTypeToColorConverter : IValueConverter
{
	// Dark theme
	private static readonly IBrush DarkUser = new SolidColorBrush(Color.Parse("#4F8EF7"));
	private static readonly IBrush DarkAssistant = new SolidColorBrush(Color.Parse("#34D399"));
	private static readonly IBrush DarkResult = new SolidColorBrush(Color.Parse("#FBBF24"));
	private static readonly IBrush DarkError = new SolidColorBrush(Color.Parse("#F87171"));
	private static readonly IBrush DarkSystem = new SolidColorBrush(Color.Parse("#A78BFA"));
	private static readonly IBrush DarkDefault = new SolidColorBrush(Color.Parse("#38FFFFFF"));

	// Light theme
	private static readonly IBrush LightUser = new SolidColorBrush(Color.Parse("#2871F0"));
	private static readonly IBrush LightAssistant = new SolidColorBrush(Color.Parse("#00A06A"));
	private static readonly IBrush LightResult = new SolidColorBrush(Color.Parse("#D97706"));
	private static readonly IBrush LightError = new SolidColorBrush(Color.Parse("#E53535"));
	private static readonly IBrush LightSystem = new SolidColorBrush(Color.Parse("#7C4DFF"));
	private static readonly IBrush LightDefault = new SolidColorBrush(Color.Parse("#420A0A12"));

	private static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is string type)
		{
			var dark = IsDark;
			return type.ToLowerInvariant() switch
			{
				"user" => dark ? DarkUser : LightUser,
				"assistant" => dark ? DarkAssistant : LightAssistant,
				"result" => dark ? DarkResult : LightResult,
				"error" => dark ? DarkError : LightError,
				"system" => dark ? DarkSystem : LightSystem,
				_ => dark ? DarkDefault : LightDefault
			};
		}
		return IsDark ? DarkDefault : LightDefault;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class MessageTypeToGradientConverter : IValueConverter
{
	// Dark theme gradients
	private static readonly LinearGradientBrush DarkUser = MakeGradient("#4F8EF7", "#7C63F5");
	private static readonly LinearGradientBrush DarkAssistant = MakeGradient("#34D399", "#059669");
	private static readonly LinearGradientBrush DarkSystem = MakeGradient("#A78BFA", "#7C63F5");
	private static readonly LinearGradientBrush DarkResult = MakeGradient("#FBBF24", "#F59E0B");
	private static readonly LinearGradientBrush DarkError = MakeGradient("#F87171", "#EF4444");

	// Light theme gradients
	private static readonly LinearGradientBrush LightUser = MakeGradient("#2871F0", "#7C4DFF");
	private static readonly LinearGradientBrush LightAssistant = MakeGradient("#00A06A", "#007A50");
	private static readonly LinearGradientBrush LightSystem = MakeGradient("#7C4DFF", "#5A3FCF");
	private static readonly LinearGradientBrush LightResult = MakeGradient("#D97706", "#B45309");
	private static readonly LinearGradientBrush LightError = MakeGradient("#E53535", "#C41F1F");

	private static readonly IBrush DarkDefault = new SolidColorBrush(Color.Parse("#38FFFFFF"));
	private static readonly IBrush LightDefault = new SolidColorBrush(Color.Parse("#420A0A12"));

	private static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is string type)
		{
			var dark = IsDark;
			return type.ToLowerInvariant() switch
			{
				"user" => dark ? DarkUser : LightUser,
				"assistant" => dark ? DarkAssistant : LightAssistant,
				"result" => dark ? DarkResult : LightResult,
				"error" => dark ? DarkError : LightError,
				"system" => dark ? DarkSystem : LightSystem,
				_ => dark ? DarkDefault : LightDefault
			};
		}
		return IsDark ? DarkDefault : LightDefault;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();

	private static LinearGradientBrush MakeGradient(string startColor, string endColor) =>
		new()
		{
			StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
			EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
			GradientStops =
			{
				new GradientStop(Color.Parse(startColor), 0),
				new GradientStop(Color.Parse(endColor), 1)
			}
		};
}

public class MessageTypeToBgBrushConverter : IValueConverter
{
	// Dark theme bubble backgrounds
	private static readonly IBrush DarkUser = new SolidColorBrush(Color.Parse("#2E4F8EF7"));
	private static readonly IBrush DarkAssistant = new SolidColorBrush(Color.Parse("#1F34D399"));
	private static readonly IBrush DarkResult = new SolidColorBrush(Color.Parse("#1AFBBF24"));
	private static readonly IBrush DarkError = new SolidColorBrush(Color.Parse("#1FF87171"));
	private static readonly IBrush DarkSystem = new SolidColorBrush(Color.Parse("#1FA78BFA"));
	private static readonly IBrush DarkDefault = new SolidColorBrush(Color.Parse("#0BFFFFFF"));

	// Light theme bubble backgrounds
	private static readonly IBrush LightUser = new SolidColorBrush(Color.Parse("#212871F0"));
	private static readonly IBrush LightAssistant = new SolidColorBrush(Color.Parse("#1700A06A"));
	private static readonly IBrush LightResult = new SolidColorBrush(Color.Parse("#17D97706"));
	private static readonly IBrush LightError = new SolidColorBrush(Color.Parse("#17E53535"));
	private static readonly IBrush LightSystem = new SolidColorBrush(Color.Parse("#177C4DFF"));
	private static readonly IBrush LightDefault = new SolidColorBrush(Color.Parse("#94FFFFFF"));

	private static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is string type)
		{
			var dark = IsDark;
			return type.ToLowerInvariant() switch
			{
				"user" => dark ? DarkUser : LightUser,
				"assistant" => dark ? DarkAssistant : LightAssistant,
				"result" => dark ? DarkResult : LightResult,
				"error" => dark ? DarkError : LightError,
				"system" => dark ? DarkSystem : LightSystem,
				_ => dark ? DarkDefault : LightDefault
			};
		}
		return IsDark ? DarkDefault : LightDefault;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class MessageTypeToBorderBrushConverter : IValueConverter
{
	// Dark theme bubble borders
	private static readonly IBrush DarkUser = new SolidColorBrush(Color.Parse("#404F8EF7"));
	private static readonly IBrush DarkAssistant = new SolidColorBrush(Color.Parse("#3334D399"));
	private static readonly IBrush DarkResult = new SolidColorBrush(Color.Parse("#2EFBBF24"));
	private static readonly IBrush DarkError = new SolidColorBrush(Color.Parse("#38F87171"));
	private static readonly IBrush DarkSystem = new SolidColorBrush(Color.Parse("#33A78BFA"));
	private static readonly IBrush DarkDefault = new SolidColorBrush(Color.Parse("#14FFFFFF"));

	// Light theme bubble borders
	private static readonly IBrush LightUser = new SolidColorBrush(Color.Parse("#382871F0"));
	private static readonly IBrush LightAssistant = new SolidColorBrush(Color.Parse("#2E00A06A"));
	private static readonly IBrush LightResult = new SolidColorBrush(Color.Parse("#2ED97706"));
	private static readonly IBrush LightError = new SolidColorBrush(Color.Parse("#2EE53535"));
	private static readonly IBrush LightSystem = new SolidColorBrush(Color.Parse("#2E7C4DFF"));
	private static readonly IBrush LightDefault = new SolidColorBrush(Color.Parse("#D1FFFFFF"));

	private static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is string type)
		{
			var dark = IsDark;
			return type.ToLowerInvariant() switch
			{
				"user" => dark ? DarkUser : LightUser,
				"assistant" => dark ? DarkAssistant : LightAssistant,
				"result" => dark ? DarkResult : LightResult,
				"error" => dark ? DarkError : LightError,
				"system" => dark ? DarkSystem : LightSystem,
				_ => dark ? DarkDefault : LightDefault
			};
		}
		return IsDark ? DarkDefault : LightDefault;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

public class SelectedProjectBgConverter : IMultiValueConverter
{
	private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#184F8EF7"));

	public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
	{
		if (values.Count >= 2
			&& values[0] is ProjectSummary current
			&& values[1] is ProjectSummary selected
			&& current.Id == selected.Id)
			return Selected;
		return Brushes.Transparent;
	}
}

public class ContentTypeToColorConverter : IValueConverter
{
	// Dark
	private static readonly IBrush DarkText = new SolidColorBrush(Color.Parse("#70FFFFFF"));
	private static readonly IBrush DarkToolUse = new SolidColorBrush(Color.Parse("#FBBF24"));
	private static readonly IBrush DarkToolResult = new SolidColorBrush(Color.Parse("#FBBF24"));
	private static readonly IBrush DarkError = new SolidColorBrush(Color.Parse("#F87171"));
	private static readonly IBrush DarkDefault = new SolidColorBrush(Color.Parse("#38FFFFFF"));

	// Light
	private static readonly IBrush LightText = new SolidColorBrush(Color.Parse("#750A0A12"));
	private static readonly IBrush LightToolUse = new SolidColorBrush(Color.Parse("#D97706"));
	private static readonly IBrush LightToolResult = new SolidColorBrush(Color.Parse("#D97706"));
	private static readonly IBrush LightError = new SolidColorBrush(Color.Parse("#E53535"));
	private static readonly IBrush LightDefault = new SolidColorBrush(Color.Parse("#420A0A12"));

	private static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is ClaudeContentItem item)
		{
			var dark = IsDark;
			if (item.IsError) return dark ? DarkError : LightError;
			return item.Type.ToLowerInvariant() switch
			{
				"text" => dark ? DarkText : LightText,
				"tool_use" => dark ? DarkToolUse : LightToolUse,
				"tool_result" => dark ? DarkToolResult : LightToolResult,
				_ => dark ? DarkDefault : LightDefault
			};
		}
		if (value is string type)
		{
			var dark = IsDark;
			return type.ToLowerInvariant() switch
			{
				"text" => dark ? DarkText : LightText,
				"tool_use" => dark ? DarkToolUse : LightToolUse,
				"tool_result" => dark ? DarkToolResult : LightToolResult,
				_ => dark ? DarkDefault : LightDefault
			};
		}
		return IsDark ? DarkDefault : LightDefault;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}

/// <summary>
/// Converts HasErrorContent bool to appropriate foreground color.
/// Returns error color if true, otherwise returns the standard text primary color.
/// </summary>
public class ErrorContentForegroundConverter : IValueConverter
{
	private static readonly IBrush DarkError = new SolidColorBrush(Color.Parse("#F87171"));
	private static readonly IBrush LightError = new SolidColorBrush(Color.Parse("#E53535"));
	private static readonly IBrush DarkTextPrimary = new SolidColorBrush(Color.Parse("#E0FFFFFF"));
	private static readonly IBrush LightTextPrimary = new SolidColorBrush(Color.Parse("#E00A0A12"));

	private static bool IsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		var dark = IsDark;
		if (value is true)
			return dark ? DarkError : LightError;
		return dark ? DarkTextPrimary : LightTextPrimary;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
