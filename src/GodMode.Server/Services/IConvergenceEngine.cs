using GodMode.Shared.Models;

namespace GodMode.Server.Services;

public interface IConvergenceEngine
{
    /// <summary>
    /// Compares manifest to current disk state and reconciles:
    /// creates missing roots/profiles, removes undeclared ones, updates changed config.
    /// </summary>
    Task<ConvergenceResult> ConvergeAsync(GodModeManifest manifest, bool force = false);
}
