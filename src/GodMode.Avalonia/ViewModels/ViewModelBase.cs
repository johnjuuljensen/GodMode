using CommunityToolkit.Mvvm.ComponentModel;

namespace GodMode.Avalonia.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
	protected INavigationService Navigation { get; }

	protected ViewModelBase(INavigationService navigationService)
	{
		Navigation = navigationService;
	}
}
