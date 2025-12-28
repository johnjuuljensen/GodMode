using GodMode.ClientBase.Services.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Interface for profile management service
/// </summary>
public interface IProfileService
{
    Task<List<Profile>> GetProfilesAsync();
    Task<Profile?> GetProfileAsync(string name);
    Task<Profile?> GetSelectedProfileAsync();
    Task SaveProfileAsync(Profile profile);
    Task DeleteProfileAsync(string name);
    Task SetSelectedProfileAsync(string name);
    string DecryptToken(string encryptedToken);
}
