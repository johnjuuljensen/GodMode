using Avalonia;
using Avalonia.Styling;

namespace GodMode.Avalonia.Services;

public interface IThemeService
{
	bool IsDark { get; }
	event Action<bool>? ThemeChanged;
	void ToggleTheme();
	void SetTheme(bool isDark);
}

public class ThemeService : IThemeService
{
	public bool IsDark { get; private set; } = true;
	public event Action<bool>? ThemeChanged;

	public void ToggleTheme() => SetTheme(!IsDark);

	public void SetTheme(bool isDark)
	{
		IsDark = isDark;

		var app = Application.Current;
		if (app == null) return;

		app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
		ThemeChanged?.Invoke(isDark);
	}
}
