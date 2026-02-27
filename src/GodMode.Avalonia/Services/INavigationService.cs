namespace GodMode.Avalonia.Services;

public interface INavigationService
{
	object? CurrentViewModel { get; }
	bool CanGoBack { get; }
	event Action? NavigationChanged;

	void SetRoot(object viewModel);
	void NavigateTo<TViewModel>(Action<TViewModel>? configure = null) where TViewModel : class;
	void GoBack();
}
