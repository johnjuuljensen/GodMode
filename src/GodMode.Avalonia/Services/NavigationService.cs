using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Avalonia.Services;

public class NavigationService : INavigationService
{
	private readonly Stack<object> _navigationStack = new();

	public object? CurrentViewModel { get; private set; }
	public bool CanGoBack => _navigationStack.Count > 0;
	public event Action? NavigationChanged;

	/// <summary>
	/// Sets the initial view without pushing to the navigation stack.
	/// </summary>
	public void SetRoot(object viewModel)
	{
		CurrentViewModel = viewModel;
	}

	public void NavigateTo<TViewModel>(Action<TViewModel>? configure = null) where TViewModel : class
	{
		if (CurrentViewModel != null)
			_navigationStack.Push(CurrentViewModel);

		var vm = App.Services.GetRequiredService<TViewModel>();
		configure?.Invoke(vm);
		CurrentViewModel = vm;
		NavigationChanged?.Invoke();
	}

	public void GoBack()
	{
		if (_navigationStack.Count > 0)
		{
			CurrentViewModel = _navigationStack.Pop();
			NavigationChanged?.Invoke();
		}
	}
}
