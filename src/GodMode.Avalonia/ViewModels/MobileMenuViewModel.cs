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
	public bool IsVoiceSupported => _shell.IsVoiceSupported;

	[RelayCommand]
	private void AddProfile()
	{
		_shell.GoBackCommand.Execute(null);
		_shell.SidebarViewModel.AddProfileCommand.Execute(null);
	}

	[RelayCommand]
	private void ToggleTheme()
	{
		_shell.ToggleThemeCommand.Execute(null);
		OnPropertyChanged(nameof(ThemeIcon));
	}

	[RelayCommand]
	private void NavigateToVoice()
	{
		// Pop menu off stack first, then navigate to voice
		_shell.GoBackCommand.Execute(null);
		_shell.NavigateToVoiceCommand.Execute(null);
	}
}
