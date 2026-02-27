using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodMode.Avalonia.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
	[ObservableProperty]
	private object? _currentView;

	[ObservableProperty]
	private bool _isVoicePanelOpen = true;

	public VoiceAssistantViewModel Voice { get; }

	public MainWindowViewModel(
		INavigationService navigationService,
		MainViewModel mainViewModel,
		VoiceAssistantViewModel voiceAssistantViewModel)
	{
		Voice = voiceAssistantViewModel;

		// Set main view as the navigation root
		navigationService.SetRoot(mainViewModel);
		CurrentView = mainViewModel;

		navigationService.NavigationChanged += () =>
		{
			CurrentView = navigationService.CurrentViewModel;
		};

		_ = mainViewModel.LoadCommand.ExecuteAsync(null);
	}

	[RelayCommand]
	private void ToggleVoicePanel() => IsVoicePanelOpen = !IsVoicePanelOpen;
}
