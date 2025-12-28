namespace GodMode.ClientBase.Services;

/// <summary>
/// Handles system notifications for project events
/// </summary>
public class NotificationService : INotificationService
{
    private readonly Dictionary<string, int> _badgeCounts = new();

    public event EventHandler<NotificationEventArgs>? NotificationRequested;
    public event EventHandler<BadgeUpdateEventArgs>? BadgeCountUpdated;

    public void NotifyProjectNeedsInput(string profileName, string hostId, string projectId, string projectName, string? question)
    {
        var title = $"Input Required: {projectName}";
        var message = question ?? "Claude is waiting for your input";

        IncrementBadgeCount(profileName, hostId);

        NotificationRequested?.Invoke(this, new NotificationEventArgs
        {
            Title = title,
            Message = message,
            ProfileName = profileName,
            HostId = hostId,
            ProjectId = projectId
        });
    }

    public void NotifyProjectError(string profileName, string hostId, string projectId, string projectName, string error)
    {
        var title = $"Error: {projectName}";
        var message = $"Project encountered an error: {error}";

        IncrementBadgeCount(profileName, hostId);

        NotificationRequested?.Invoke(this, new NotificationEventArgs
        {
            Title = title,
            Message = message,
            ProfileName = profileName,
            HostId = hostId,
            ProjectId = projectId,
            IsError = true
        });
    }

    public void NotifyProjectCompleted(string profileName, string hostId, string projectId, string projectName)
    {
        var title = $"Completed: {projectName}";
        var message = "Project has finished execution";

        NotificationRequested?.Invoke(this, new NotificationEventArgs
        {
            Title = title,
            Message = message,
            ProfileName = profileName,
            HostId = hostId,
            ProjectId = projectId
        });
    }

    public int GetBadgeCount(string profileName, string hostId)
    {
        var key = $"{profileName}:{hostId}";
        return _badgeCounts.GetValueOrDefault(key, 0);
    }

    public int GetTotalBadgeCount()
    {
        return _badgeCounts.Values.Sum();
    }

    public void ClearBadgeCount(string profileName, string hostId)
    {
        var key = $"{profileName}:{hostId}";
        _badgeCounts[key] = 0;

        BadgeCountUpdated?.Invoke(this, new BadgeUpdateEventArgs
        {
            ProfileName = profileName,
            HostId = hostId,
            Count = 0,
            TotalCount = GetTotalBadgeCount()
        });
    }

    public void ClearBadgeCountForProject(string profileName, string hostId, string projectId)
    {
        // For now, just decrement the count
        var key = $"{profileName}:{hostId}";
        if (_badgeCounts.ContainsKey(key) && _badgeCounts[key] > 0)
        {
            _badgeCounts[key]--;

            BadgeCountUpdated?.Invoke(this, new BadgeUpdateEventArgs
            {
                ProfileName = profileName,
                HostId = hostId,
                Count = _badgeCounts[key],
                TotalCount = GetTotalBadgeCount()
            });
        }
    }

    private void IncrementBadgeCount(string profileName, string hostId)
    {
        var key = $"{profileName}:{hostId}";
        _badgeCounts[key] = _badgeCounts.GetValueOrDefault(key, 0) + 1;

        BadgeCountUpdated?.Invoke(this, new BadgeUpdateEventArgs
        {
            ProfileName = profileName,
            HostId = hostId,
            Count = _badgeCounts[key],
            TotalCount = GetTotalBadgeCount()
        });
    }
}

public class NotificationEventArgs : EventArgs
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string HostId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public bool IsError { get; set; }
}

public class BadgeUpdateEventArgs : EventArgs
{
    public string ProfileName { get; set; } = string.Empty;
    public string HostId { get; set; } = string.Empty;
    public int Count { get; set; }
    public int TotalCount { get; set; }
}
