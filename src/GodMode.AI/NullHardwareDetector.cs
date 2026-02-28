namespace GodMode.AI;

/// <summary>
/// Default hardware detector for platforms without specialized detection.
/// Reports only CPU as available.
/// </summary>
public sealed class NullHardwareDetector : IHardwareDetector
{
    private static readonly IReadOnlySet<string> CpuOnly = new HashSet<string> { "cpu" };

    public IReadOnlySet<string> DetectAvailableProviders() => CpuOnly;
}
