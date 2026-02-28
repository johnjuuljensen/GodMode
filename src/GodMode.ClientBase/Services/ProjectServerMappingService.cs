using System.Text.Json;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Persists project-to-server mappings in ~/.godmode/project-servers.json.
/// </summary>
public class ProjectServerMappingService : IProjectServerMappingService
{
	private const string FileName = "project-servers.json";
	private readonly string _filePath;
	private Dictionary<string, string>? _cache;

	public ProjectServerMappingService(string appDataPath)
	{
		_filePath = Path.Combine(appDataPath, FileName);
	}

	public async Task<string?> GetServerIdAsync(string projectId)
	{
		var map = await LoadAsync();
		return map.TryGetValue(projectId, out var serverId) ? serverId : null;
	}

	public async Task SetServerIdAsync(string projectId, string serverId)
	{
		var map = await LoadAsync();
		map[projectId] = serverId;
		await SaveAsync(map);
	}

	public async Task RemoveAsync(string projectId)
	{
		var map = await LoadAsync();
		if (map.Remove(projectId))
			await SaveAsync(map);
	}

	private async Task<Dictionary<string, string>> LoadAsync()
	{
		if (_cache != null)
			return _cache;

		if (!File.Exists(_filePath))
		{
			_cache = new Dictionary<string, string>();
			return _cache;
		}

		try
		{
			var json = await File.ReadAllTextAsync(_filePath);
			_cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
		}
		catch
		{
			_cache = new Dictionary<string, string>();
		}

		return _cache;
	}

	private async Task SaveAsync(Dictionary<string, string> map)
	{
		_cache = map;
		var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
		await File.WriteAllTextAsync(_filePath, json);
	}
}
