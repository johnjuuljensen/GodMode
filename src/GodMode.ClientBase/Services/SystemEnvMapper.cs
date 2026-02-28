namespace GodMode.ClientBase.Services;

/// <summary>
/// Maps system names to well-known environment variable names.
/// </summary>
public static class SystemEnvMapper
{
	private static readonly Dictionary<string, string> WellKnownMappings = new(StringComparer.OrdinalIgnoreCase)
	{
		["anthropic"] = "ANTHROPIC_API_KEY",
		["github"] = "GITHUB_TOKEN",
		["openai"] = "OPENAI_API_KEY",
		["aws"] = "AWS_SECRET_ACCESS_KEY",
		["azure"] = "AZURE_API_KEY",
		["google"] = "GOOGLE_API_KEY",
		["huggingface"] = "HUGGINGFACE_TOKEN",
		["replicate"] = "REPLICATE_API_TOKEN",
	};

	/// <summary>
	/// Gets the environment variable name for a system.
	/// Uses well-known mappings for common systems, otherwise GODMODE_SECRET_{SYSTEM_NAME}.
	/// </summary>
	public static string GetEnvVarName(string systemName)
	{
		return WellKnownMappings.TryGetValue(systemName, out var envVar)
			? envVar
			: $"GODMODE_SECRET_{systemName.ToUpperInvariant()}";
	}
}
