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

	public event Action<ServerGroupViewModel, ProjectSummary>? ProjectSelected;
	public event Action<ServerGroupViewModel, string?>? CreateProjectRequested;

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
					var tile = new ProjectTileViewModel
					{
						Summary = project,
						ServerGroup = server
					};
					Tiles.Add(tile);

					// Load messages in background
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

			// Give it a moment to load, then mark as done
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
		ProjectSelected?.Invoke(tile.ServerGroup, tile.Summary);
	}

	[RelayCommand]
	private void CreateProject()
	{
		var server = _connectedServers.FirstOrDefault();
		if (server != null)
			CreateProjectRequested?.Invoke(server, null);
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

	[ObservableProperty]
	private ObservableCollection<ClaudeMessage> _messages = new();

	[ObservableProperty]
	private bool _isLoadingMessages;
}
