using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GodMode.Avalonia.ViewModels;

public partial class DeleteConfirmViewModel : ObservableObject
{
	[ObservableProperty]
	private string _projectName = string.Empty;

	[ObservableProperty]
	private bool _isForce;

	public event Action<bool, bool>? Completed;

	[RelayCommand]
	private void Confirm() => Completed?.Invoke(true, IsForce);

	[RelayCommand]
	private void Cancel() => Completed?.Invoke(false, false);
}
