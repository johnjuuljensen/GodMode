namespace GodMode.AI;

/// <summary>
/// Detects which inference execution providers are available on the current hardware.
/// Platform projects implement this to probe for NPU/DirectML/etc.
/// </summary>
public interface IHardwareDetector
{
    IReadOnlySet<string> DetectAvailableProviders();
}
