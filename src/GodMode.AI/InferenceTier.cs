namespace GodMode.AI;

/// <summary>
/// Inference task weight tiers. Light tasks (quick classification, routing)
/// can run on NPU; Medium/Heavy tasks (conversation, generation) need GPU/CPU.
/// </summary>
public enum InferenceTier
{
    Light,
    Medium,
    Heavy
}
