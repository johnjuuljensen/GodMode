using System.Security.Cryptography;
using System.Text;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Protects tokens using OS-level DPAPI on Windows, plaintext fallback elsewhere.
/// Stored format: "dpapi:" + base64 (Windows) or "plain:" + raw token (other platforms).
/// Tokens without a recognized prefix are treated as plaintext (legacy compatibility).
/// </summary>
public class TokenProtector : ITokenProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    public string Protect(string token)
    {
        if (string.IsNullOrEmpty(token))
            return token;

        if (OperatingSystem.IsWindows())
        {
            var tokenBytes = Encoding.UTF8.GetBytes(token);
            var protectedBytes = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(protectedBytes);
        }

        return PlainPrefix + token;
    }

    public string Unprotect(string storedToken)
    {
        if (string.IsNullOrEmpty(storedToken))
            return storedToken;

        if (storedToken.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DPAPI tokens can only be unprotected on Windows.");

            var base64 = storedToken[DpapiPrefix.Length..];
            var protectedBytes = Convert.FromBase64String(base64);
            var tokenBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(tokenBytes);
        }

        if (storedToken.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return storedToken[PlainPrefix.Length..];

        // No recognized prefix — treat as plaintext (legacy data)
        return storedToken;
    }
}
