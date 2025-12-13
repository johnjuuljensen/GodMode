namespace GodMode.Maui.Services;

/// <summary>
/// Interface for notification service
/// </summary>
public interface INotificationService
{
    event EventHandler<NotificationEventArgs>? NotificationRequested;
    event EventHandler<BadgeUpdateEventArgs>? BadgeCountUpdated;

    void NotifyProjectNeedsInput(string profileName, string hostId, string projectId, string projectName, string? question);
    void NotifyProjectError(string profileName, string hostId, string projectId, string projectName, string error);
    void NotifyProjectCompleted(string profileName, string hostId, string projectId, string projectName);
    int GetBadgeCount(string profileName, string hostId);
    int GetTotalBadgeCount();
    void ClearBadgeCount(string profileName, string hostId);
    void ClearBadgeCountForProject(string profileName, string hostId, string projectId);
}
