using CommunityToolkit.Mvvm.ComponentModel;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Avalonia.ViewModels;

public partial class ServerGroupViewModel : ObservableObject
{
	[ObservableProperty]
	private string _id = string.Empty;

	[ObservableProperty]
	private string _name = string.Empty;

	[ObservableProperty]
	private string _type = string.Empty;

	[ObservableProperty]
	private HostState _state;

	[ObservableProperty]
	private string? _url;

	[ObservableProperty]
	private string _profileName = string.Empty;

	[ObservableProperty]
	private int _accountIndex;

	[ObservableProperty]
	private bool _isExpanded = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusDisplay))]
	[NotifyPropertyChangedFor(nameof(CanConnect))]
	private bool _isConnected;

	[ObservableProperty]
	private bool _isLoadingProjects;

	[ObservableProperty]
	private string? _errorMessage;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ProjectCountDisplay))]
	private ObservableCollection<ProjectSummary> _projects = new();

	public string StatusDisplay => IsConnected ? "Online" : State switch
	{
		HostState.Running => "Online",
		HostState.Stopped => "Offline",
		HostState.Starting => "Starting...",
		HostState.Stopping => "Stopping...",
		HostState.Unknown => "Unknown",
		_ => "Unknown"
	};

	public bool CanConnect => IsConnected || State == HostState.Running;

	public string ProjectCountDisplay => Projects.Count switch
	{
		0 => "No projects",
		1 => "1 project",
		_ => $"{Projects.Count} projects"
	};

	public static ServerGroupViewModel FromHostInfo(HostInfo host, string profileName, int accountIndex) => new()
	{
		Id = host.Id,
		Name = host.Name,
		Type = host.Type,
		State = host.State,
		Url = host.Url,
		ProfileName = profileName,
		AccountIndex = accountIndex
	};
}
