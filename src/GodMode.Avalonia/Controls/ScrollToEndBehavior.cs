using Avalonia;
using Avalonia.Controls;

namespace GodMode.Avalonia.Controls;

/// <summary>
/// Attached behavior that auto-scrolls a ScrollViewer to the end whenever its content size changes.
/// Usage: <ScrollViewer controls:ScrollToEndBehavior.IsEnabled="True" />
/// </summary>
public class ScrollToEndBehavior
{
	public static readonly AttachedProperty<bool> IsEnabledProperty =
		AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(ScrollToEndBehavior));

	public static bool GetIsEnabled(ScrollViewer sv) => sv.GetValue(IsEnabledProperty);
	public static void SetIsEnabled(ScrollViewer sv, bool value) => sv.SetValue(IsEnabledProperty, value);

	static ScrollToEndBehavior()
	{
		IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((sv, _) =>
		{
			if (GetIsEnabled(sv))
			{
				sv.PropertyChanged += (_, e) =>
				{
					if (e.Property == ScrollViewer.ExtentProperty)
						sv.ScrollToEnd();
				};
			}
		});
	}
}
