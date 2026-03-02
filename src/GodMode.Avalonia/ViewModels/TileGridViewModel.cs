using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class TileGridViewModel : ViewModelBase
{
	private readonly IProjectService _projectService;
	private readonly List<IDisposable> _subscriptions = new();

	[ObservableProperty]
	private ObservableCollection<ProjectTileViewModel> _tiles = new();

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private bool _hasConnectedServers;

	private List<ServerGroupViewModel> _connectedServers = new();

	public event Action<RootGroupViewModel, ProjectSummary>? ProjectSelected;
	public event Action<RootGroupViewModel, string?>? CreateProjectRequested;

	public TileGridViewModel(
		INavigationService navigationService,
		IProjectService projectService)
		: base(navigationService)
	{
		_projectService = projectService;
	}

	public async Task LoadAsync(IEnumerable<ServerGroupViewModel> servers)
	{
		IsLoading = true;
		DisposeSubscriptions();
		Tiles.Clear();

		try
		{
			_connectedServers = servers.Where(s => s.IsConnected).ToList();
			HasConnectedServers = _connectedServers.Count > 0;

			foreach (var server in _connectedServers)
			{
				foreach (var project in server.Projects)
				{
					// Find the root group this project belongs to
					var rootGroup = server.RootGroups
						.FirstOrDefault(r => r.Projects.Any(p => p.Id == project.Id));

					var tile = new ProjectTileViewModel
					{
						Summary = project,
						ServerGroup = server,
						RootGroup = rootGroup
					};
					Tiles.Add(tile);

					_ = LoadTileMessagesAsync(tile);
				}
			}
		}
		finally
		{
			IsLoading = false;
		}
	}

	private async Task LoadTileMessagesAsync(ProjectTileViewModel tile)
	{
		try
		{
			tile.IsLoadingMessages = true;

			var observable = await _projectService.SubscribeOutputAsync(
				tile.ServerGroup.ProfileName,
				tile.ServerGroup.Id,
				tile.Summary.Id,
				fromOffset: 0);

			var subscription = observable.Subscribe(
				onNext: message =>
				{
					Dispatcher.UIThread.Post(() =>
					{
						tile.Messages.Add(message);
					});
				},
				onError: _ =>
				{
					Dispatcher.UIThread.Post(() => tile.IsLoadingMessages = false);
				},
				onCompleted: () =>
				{
					Dispatcher.UIThread.Post(() => tile.IsLoadingMessages = false);
				});

			_subscriptions.Add(subscription);

			await Task.Delay(2000);
			Dispatcher.UIThread.Post(() => tile.IsLoadingMessages = false);
		}
		catch
		{
			tile.IsLoadingMessages = false;
		}
	}

	[RelayCommand]
	private void SelectProject(ProjectTileViewModel tile)
	{
		if (tile.RootGroup != null)
			ProjectSelected?.Invoke(tile.RootGroup, tile.Summary);
	}

	[RelayCommand]
	private void CreateProject()
	{
		// Find first connected root group
		var server = _connectedServers.FirstOrDefault();
		var root = server?.RootGroups.FirstOrDefault();
		if (root != null)
			CreateProjectRequested?.Invoke(root, null);
	}

	public void Cleanup()
	{
		DisposeSubscriptions();
	}

	private void DisposeSubscriptions()
	{
		foreach (var sub in _subscriptions)
			sub.Dispose();
		_subscriptions.Clear();
	}
}

public partial class ProjectTileViewModel : ObservableObject
{
	[ObservableProperty]
	private ProjectSummary _summary = null!;

	[ObservableProperty]
	private ServerGroupViewModel _serverGroup = null!;

	/// <summary>
	/// The root group this tile's project belongs to. Used for navigation.
	/// </summary>
	public RootGroupViewModel? RootGroup { get; set; }

	[ObservableProperty]
	private ObservableCollection<ClaudeMessage> _messages = new();

	[ObservableProperty]
	private bool _isLoadingMessages;
}
