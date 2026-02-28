using CommunityToolkit.Mvvm.ComponentModel;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class RootGroupViewModel : ObservableObject
{
	[ObservableProperty]
	private string _name = string.Empty;

	[ObservableProperty]
	private bool _isExpanded = true;

	[ObservableProperty]
	private ObservableCollection<ProjectSummary> _projects = new();
}
