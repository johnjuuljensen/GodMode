namespace GodMode.AI.LocalInference.Windows;

/// <summary>
/// Windows hardware detector. DirectML is available on Windows 10+.
/// NPU availability is reported when an NPU model path is configured
/// (actual VitisAI availability is tested at model load time by NpuOnnxModel's fallback logic).
/// </summary>
public sealed class WinHardwareDetector : IHardwareDetector
{
    public IReadOnlySet<string> DetectAvailableProviders()
    {
        var providers = new HashSet<string> { "directml", "cpu" };

        var config = AIConfig.Load();
        if (!string.IsNullOrEmpty(config.NpuModelPath))
            providers.Add("npu");

        return providers;
    }
}
