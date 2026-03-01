using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GodMode.Avalonia.ViewModels;

namespace GodMode.Avalonia.Views;

public partial class CreateProjectView : UserControl
{
	public CreateProjectView()
	{
		InitializeComponent();
		AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
	}

	private void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
		{
			if (DataContext is CreateProjectViewModel vm && vm.CreateCommand.CanExecute(null))
			{
				_ = vm.CreateCommand.ExecuteAsync(null);
				e.Handled = true;
			}
		}
	}
}
