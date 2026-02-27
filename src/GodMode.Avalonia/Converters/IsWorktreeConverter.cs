using System.Globalization;
using Avalonia.Data.Converters;
using GodMode.Shared.Enums;

namespace GodMode.Avalonia.Converters;

public class IsWorktreeConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is ProjectType projectType)
			return projectType == ProjectType.GitHubWorktree;
		return false;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
		=> throw new NotImplementedException();
}
