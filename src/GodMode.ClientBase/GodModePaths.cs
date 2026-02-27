namespace GodMode.ClientBase;

/// <summary>
/// Shared configuration paths for all GodMode client applications.
/// Data is stored in ~/.godmode so all clients (MAUI, Avalonia, etc.) share the same config.
/// </summary>
public static class GodModePaths
{
	/// <summary>
	/// The root directory for GodMode configuration and data (~/.godmode).
	/// </summary>
	public static string AppDataDirectory { get; } = GetAppDataDirectory();

	private static string GetAppDataDirectory()
	{
		var path = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".godmode");
		Directory.CreateDirectory(path);
		return path;
	}
}
