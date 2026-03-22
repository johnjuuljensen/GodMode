using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodMode.Avalonia.ViewModels;

public partial class MobileMenuViewModel : ObservableObject
{
	private readonly MainWindowViewModel _shell;

	public MobileMenuViewModel(MainWindowViewModel shell)
	{
		_shell = shell;
	}

	public string ThemeIcon => _shell.ThemeIcon;

	[RelayCommand]
	private void AddServer()
	{
		_shell.GoBackCommand.Execute(null);
		_shell.SidebarViewModel.AddServerCommand.Execute(null);
	}

	[RelayCommand]
	private void ToggleTheme()
	{
		_shell.ToggleThemeCommand.Execute(null);
		OnPropertyChanged(nameof(ThemeIcon));
	}
}
