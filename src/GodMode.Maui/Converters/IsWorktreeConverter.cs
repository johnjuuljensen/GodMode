using System.Globalization;
using GodMode.Shared.Enums;

namespace GodMode.Maui.Converters;

/// <summary>
/// Converts ProjectType to boolean, returning true if the type is GitHubWorktree.
/// </summary>
public class IsWorktreeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ProjectType projectType)
            return projectType == ProjectType.GitHubWorktree;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
