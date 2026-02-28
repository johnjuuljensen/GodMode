namespace GodMode.ClientBase.Services;

/// <summary>
/// Tracks which server each project belongs to.
/// Stored in ~/.godmode/project-servers.json.
/// </summary>
public interface IProjectServerMappingService
{
	Task<string?> GetServerIdAsync(string projectId);
	Task SetServerIdAsync(string projectId, string serverId);
	Task RemoveAsync(string projectId);
}
