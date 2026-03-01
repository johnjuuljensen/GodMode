namespace GodMode.AI.LocalInference.Windows;

/// <summary>
/// Windows hardware detector. DirectML is available on Windows 10+.
/// </summary>
public sealed class WinHardwareDetector : IHardwareDetector
{
    public IReadOnlySet<string> DetectAvailableProviders()
    {
        return new HashSet<string> { "directml", "cpu" };
    }
}
