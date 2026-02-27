using CommunityToolkit.Mvvm.ComponentModel;

namespace GodMode.Avalonia.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
	[ObservableProperty]
	private object? _currentView;

	public MainWindowViewModel(INavigationService navigationService, MainViewModel mainViewModel)
	{
		// Set main view as the navigation root
		navigationService.SetRoot(mainViewModel);
		CurrentView = mainViewModel;

		navigationService.NavigationChanged += () =>
		{
			CurrentView = navigationService.CurrentViewModel;
		};

		_ = mainViewModel.LoadCommand.ExecuteAsync(null);
	}
}
