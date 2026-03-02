using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

/// <summary>
/// Groups roots by server-discovered profile name.
/// This is the top-level grouping in the sidebar.
/// </summary>
public partial class ProfileGroupViewModel : ObservableObject
{
	[ObservableProperty]
	private string _name = string.Empty;

	[ObservableProperty]
	private bool _isExpanded = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ProjectCount))]
	private ObservableCollection<RootGroupViewModel> _rootGroups = new();

	public int ProjectCount => RootGroups.Sum(r => r.Projects.Count);

	public string ProjectCountDisplay => ProjectCount switch
	{
		0 => "No projects",
		1 => "1 project",
		_ => $"{ProjectCount} projects"
	};
}
