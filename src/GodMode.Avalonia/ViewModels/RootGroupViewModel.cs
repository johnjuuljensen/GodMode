using CommunityToolkit.Mvvm.ComponentModel;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public record RootActionItem(RootGroupViewModel Root, CreateActionInfo Action);

public partial class RootGroupViewModel : ObservableObject
{
	[ObservableProperty]
	private string _name = string.Empty;

	[ObservableProperty]
	private bool _isExpanded = true;

	[ObservableProperty]
	private ObservableCollection<ProjectSummary> _projects = new();

	public ServerGroupViewModel Server { get; set; } = null!;

	public IReadOnlyList<RootActionItem> ActionItems { get; set; } = [];

	public bool HasActions => ActionItems.Count > 0;
}
