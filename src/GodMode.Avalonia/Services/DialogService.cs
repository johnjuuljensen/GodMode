using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using GodMode.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Avalonia.Services;

public class DialogService : IDialogService
{
	private static Window? GetMainWindow()
	{
		if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			return desktop.MainWindow;
		return null;
	}

	public async Task<bool> ConfirmAsync(string title, string message, string accept = "OK", string cancel = "Cancel")
	{
		var window = GetMainWindow();
		if (window == null) return false;

		var result = false;
		var dialog = new Window
		{
			Title = title,
			Width = 400,
			Height = 180,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			CanResize = false,
			Content = new StackPanel
			{
				Margin = new Thickness(20),
				Spacing = 20,
				Children =
				{
					new TextBlock
					{
						Text = message,
						TextWrapping = TextWrapping.Wrap
					},
					new StackPanel
					{
						Orientation = Orientation.Horizontal,
						HorizontalAlignment = HorizontalAlignment.Right,
						Spacing = 10,
						Children =
						{
							new Button { Content = cancel, Tag = "cancel" },
							new Button { Content = accept, Tag = "accept" }
						}
					}
				}
			}
		};

		// Wire up buttons
		if (dialog.Content is StackPanel outer && outer.Children[1] is StackPanel buttons)
		{
			foreach (var child in buttons.Children)
			{
				if (child is Button btn)
				{
					btn.Click += (_, _) =>
					{
						result = btn.Tag as string == "accept";
						dialog.Close();
					};
				}
			}
		}

		await dialog.ShowDialog(window);
		return result;
	}

	public Task<(bool Confirmed, bool Force)> ConfirmDeleteAsync(string projectName)
	{
		var window = GetMainWindow();
		if (window == null) return Task.FromResult((false, false));

		var tcs = new TaskCompletionSource<(bool, bool)>();

		var vm = App.Services.GetRequiredService<DeleteConfirmViewModel>();
		vm.ProjectName = projectName;
		vm.Completed += (confirmed, force) =>
		{
			if (window.DataContext is MainWindowViewModel mwvm)
			{
				mwvm.IsModalVisible = false;
				mwvm.ModalViewModel = null;
			}
			tcs.TrySetResult((confirmed, force));
		};

		if (window.DataContext is MainWindowViewModel mainVm)
		{
			mainVm.ModalViewModel = vm;
			mainVm.IsModalVisible = true;
		}

		return tcs.Task;
	}

	public async Task AlertAsync(string title, string message)
	{
		await ConfirmAsync(title, message, "OK", "");
	}
}
