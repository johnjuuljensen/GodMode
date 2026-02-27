namespace GodMode.Voice.Tools;

public sealed class GeneralStatusTool : ITool
{
    public string Name => "general_status";
    public string Description => "Returns the general status of the system including active profile, loaded models, and uptime.";

    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();

    public Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var status = new
        {
            ActiveProfile = _currentProfile,
            ModelsLoaded = _modelsLoaded,
            Uptime = (DateTime.UtcNow - _startTime).ToString(@"hh\:mm\:ss"),
            Status = "operational"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(status,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        return Task.FromResult(ToolResult.Ok(Name, json));
    }

    // Simple in-memory state for demo purposes
    private static string _currentProfile = "default";
    private static readonly DateTime _startTime = DateTime.UtcNow;
    private static int _modelsLoaded = 0;

    internal static void SetProfile(string profile) => _currentProfile = profile;
    internal static void SetModelsLoaded(int count) => _modelsLoaded = count;
}
