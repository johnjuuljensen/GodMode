using CommunityToolkit.Mvvm.ComponentModel;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using System.Collections.ObjectModel;

namespace GodMode.Maui.ViewModels;

/// <summary>
/// View model representing a server with its projects for grouped display on the main page.
/// </summary>
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
    private ObservableCollection<ProjectSummary> _projects = new();

    /// <summary>
    /// Gets a display string for the server status.
    /// </summary>
    public string StatusDisplay => IsConnected ? "Online" : State switch
    {
        HostState.Running => "Online",
        HostState.Stopped => "Offline",
        HostState.Starting => "Starting...",
        HostState.Stopping => "Stopping...",
        HostState.Unknown => "Unknown",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets whether the server can be connected to (is running or already connected).
    /// </summary>
    public bool CanConnect => IsConnected || State == HostState.Running;

    /// <summary>
    /// Gets a display label showing project count.
    /// </summary>
    public string ProjectCountDisplay => Projects.Count switch
    {
        0 => "No projects",
        1 => "1 project",
        _ => $"{Projects.Count} projects"
    };

    /// <summary>
    /// Creates a ServerGroupViewModel from a HostInfo.
    /// </summary>
    public static ServerGroupViewModel FromHostInfo(HostInfo host, string profileName, int accountIndex)
    {
        return new ServerGroupViewModel
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
}
