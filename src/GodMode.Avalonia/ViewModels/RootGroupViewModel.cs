using CommunityToolkit.Mvvm.ComponentModel;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public record RootActionItem(RootGroupViewModel Root, CreateActionInfo Action);

public partial class RootGroupViewModel : ObservableObject
{
	/// <summary>
	/// Display name for UI (e.g. "main (Local Server)").
	/// </summary>
	[ObservableProperty]
	private string _name = string.Empty;

	/// <summary>
	/// Actual root name for API calls.
	/// </summary>
	[ObservableProperty]
	private string _rootName = string.Empty;

	/// <summary>
	/// Server-discovered profile name.
	/// </summary>
	[ObservableProperty]
	private string _profileName = string.Empty;

	/// <summary>
	/// Host identifier for connection operations.
	/// </summary>
	[ObservableProperty]
	private string _hostId = string.Empty;

	/// <summary>
	/// Display name of the host/server.
	/// </summary>
	[ObservableProperty]
	private string _hostDisplayName = string.Empty;

	[ObservableProperty]
	private bool _isConnected;

	[ObservableProperty]
	private bool _isExpanded = true;

	[ObservableProperty]
	private ObservableCollection<ProjectSummary> _projects = new();

	/// <summary>
	/// Back-reference to the server for connection tracking.
	/// </summary>
	public ServerGroupViewModel? Server { get; set; }

	public IReadOnlyList<RootActionItem> ActionItems { get; set; } = [];

	public bool HasActions => ActionItems.Count > 0;
}
