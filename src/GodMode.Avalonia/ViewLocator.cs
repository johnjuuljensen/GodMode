using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace GodMode.Avalonia;

public class ViewLocator : IDataTemplate
{
	public Control Build(object? param)
	{
		if (param is null)
			return new TextBlock { Text = "No view model" };

		var vmName = param.GetType().FullName!;
		var viewName = vmName
			.Replace("ViewModel", "View")
			.Replace(".ViewModels.", ".Views.");

		var type = Type.GetType(viewName);
		if (type != null)
			return (Control)Activator.CreateInstance(type)!;

		return new TextBlock { Text = $"Not Found: {viewName}" };
	}

	public bool Match(object? data) => data is ViewModelBase or MainWindowViewModel
		or VoiceAssistantViewModel or DeleteConfirmViewModel;
}
