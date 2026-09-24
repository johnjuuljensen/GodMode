using System.Security.Cryptography;
using System.Text;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Reads the tokens older builds wrote into servers.json: "dpapi:" + base64 (Windows), "plain:" + token,
/// or a bare token. GitHub tokens added from React were protected twice, so prefixes are unwrapped repeatedly.
/// </summary>
internal static class LegacyToken
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";

    public static string Unprotect(string stored)
    {
        while (true)
        {
            if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
                stored = stored[PlainPrefix.Length..];
            else if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
                stored = OperatingSystem.IsWindows()
                    ? Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        Convert.FromBase64String(stored[DpapiPrefix.Length..]), null, DataProtectionScope.CurrentUser))
                    : throw new PlatformNotSupportedException("DPAPI tokens can only be unprotected on Windows.");
            else
                return stored;
        }
    }
}
