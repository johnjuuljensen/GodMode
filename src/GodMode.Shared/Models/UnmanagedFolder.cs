using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// A folder in a root that no session works in, which <see cref="Hubs.IProjectHub.AdoptFolder"/> can make
/// a session of: one of the root's <c>list</c> script's candidates, or, for a root without one, one of its
/// immediate subfolders. <see cref="Hubs.IProjectHub.ListUnmanaged"/> lists them.
/// </summary>
/// <param name="Path">The folder, relative to its root: an immediate subfolder's name. What <see cref="Hubs.IProjectHub.AdoptFolder"/> takes back.</param>
/// <param name="Name">What the app shows it as, and what a session adopted with no script is called: the script's <c>name</c>, else the folder's name.</param>
/// <param name="Kind">What it is (<c>feat</c>, <c>bug</c>…), the script's <c>kind</c>; null when it gave none.</param>
/// <param name="ActionName">The root's action that adopts it, the script's <c>action</c>; null for the root's first action.</param>
/// <param name="Inputs">The inputs the adopt passes on (the branch, an issue number), the script's <c>inputs</c>; null when it gave none.</param>
public record UnmanagedFolder(
    string Path,
    string Name,
    string? Kind = null,
    string? ActionName = null,
    Dictionary<string, JsonElement>? Inputs = null
);
