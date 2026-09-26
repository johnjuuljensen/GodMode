using System.Security.AccessControl;
using System.Security.Principal;
using GodMode.Server.Auth;
using Microsoft.Extensions.Configuration;

namespace GodMode.Server.Tests;

/// <summary>
/// The auth mode and where its key comes from: a codespace authenticates GitHub tokens; anywhere else
/// it is the configured API key, else the one generated into the key file, owner-only, on the first start.
/// </summary>
public sealed class AuthModeSelectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"godmode-authmode-{Guid.NewGuid():N}");

    private string KeyFile => Path.Combine(_dir, "data", "api-key");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ProjectRootsDir"] = Path.Combine(_dir, "roots"),
                [ApiKeyFile.PathSetting] = KeyFile,
            }.Concat(settings.Select(s => KeyValuePair.Create(s.Key, s.Value)))
            .GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Last().Value)).Build();

    [Fact]
    public void Codespace_WinsOverAKey_AndCarriesTheUserAndTheCodespacesOwnToken()
    {
        var settings = AuthModeSelector.Resolve(Config(
            ("CODESPACES", "true"), ("GITHUB_USER", "octocat"), ("GITHUB_TOKEN", "ghu_codespace"),
            (AuthModeSelector.ApiKeySetting, "ignored-in-codespace")));

        Assert.Equal(new AuthSettings(AuthMode.Codespace, GitHubUser: "octocat", CodespaceToken: "ghu_codespace"), settings);
        Assert.False(File.Exists(KeyFile));
    }

    [Fact]
    public void ConfiguredKey_Wins_AndNoKeyFileIsWritten()
    {
        var settings = AuthModeSelector.Resolve(Config((AuthModeSelector.ApiKeySetting, "secret")));

        Assert.Equal(new AuthSettings(AuthMode.ApiKey, "secret"), settings);
        Assert.False(File.Exists(KeyFile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoKey_GeneratesOneIntoTheKeyFile_AndTheNextStartReusesIt(string? configured)
    {
        var first = AuthModeSelector.Resolve(Config((AuthModeSelector.ApiKeySetting, configured)));

        Assert.Equal(AuthMode.ApiKey, first.Mode);
        Assert.Matches("^[0-9a-f]{64}$", first.ApiKey); // 256 bits
        Assert.Equal((KeyFile, true), (first.KeyFilePath, first.KeyFileCreated));
        Assert.Equal(first.ApiKey, File.ReadAllText(KeyFile));

        var second = AuthModeSelector.Resolve(Config((AuthModeSelector.ApiKeySetting, configured)));
        Assert.Equal((first.ApiKey, KeyFile, false), (second.ApiKey, second.KeyFilePath, second.KeyFileCreated));
    }

    [Fact]
    public void KeyFile_IsReadableByTheServersUserAlone()
    {
        ApiKeyFile.LoadOrCreate(KeyFile);

        if (OperatingSystem.IsWindows())
        {
            var acl = new FileInfo(KeyFile).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected, "the key file inherits its directory's ACL");
            var rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        }
    }

    [Fact]
    public void KeyFileWithoutAKey_IsReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllText(KeyFile, "  \n");

        var (key, created) = ApiKeyFile.LoadOrCreate(KeyFile);

        Assert.True(created);
        Assert.Equal(key, File.ReadAllText(KeyFile));
    }

    [Fact]
    public void KeyFile_KeyWrittenByHand_IsUsedTrimmed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllText(KeyFile, "my-own-key\n");

        Assert.Equal(("my-own-key", false), ApiKeyFile.LoadOrCreate(KeyFile));
    }

    /// <summary>Sessions work under ProjectRootsDir: the key file is never there.</summary>
    [Theory]
    [InlineData("roots/api-key")]
    [InlineData("roots/.profiles/api-key")]
    [InlineData("roots/some-root/project/.godmode/api-key")]
    public void KeyFileUnderProjectRootsDir_IsRefused(string relative)
    {
        var path = Path.Combine(_dir, relative);

        var ex = Assert.Throws<StartupConfigurationException>(() => AuthModeSelector.Resolve(Config((ApiKeyFile.PathSetting, path))));

        Assert.Contains("ProjectRootsDir", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void KeyFileBesideProjectRootsDir_IsNotUnderIt()
    {
        // "roots-data" starts with "roots" but is not inside it
        var path = Path.Combine(_dir, "roots-data", "api-key");

        Assert.Equal(path, AuthModeSelector.Resolve(Config((ApiKeyFile.PathSetting, path))).KeyFilePath);
    }

    [Fact]
    public void KeyFileThatCannotBeWritten_FailsWithWhatToSet()
    {
        // A directory where the file should be
        Directory.CreateDirectory(KeyFile);

        var ex = Assert.Throws<StartupConfigurationException>(() => AuthModeSelector.Resolve(Config()));

        Assert.Contains(KeyFile, ex.Message);
        Assert.Contains(AuthModeSelector.ApiKeySetting, ex.Message);
    }

    [Fact]
    public void DefaultKeyFile_IsInTheUsersLocalApplicationData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        Assert.Equal(Path.Combine(localAppData, "GodMode.Server", "api-key"), ApiKeyFile.DefaultPath());
    }

    [Fact]
    public void Settings_NameNoSecretWhenLogged()
    {
        var settings = new AuthSettings(AuthMode.ApiKey, "the-api-key", CodespaceToken: "ghu_token", KeyFilePath: "/data/api-key");

        Assert.DoesNotContain("the-api-key", settings.ToString());
        Assert.DoesNotContain("ghu_token", settings.ToString());
    }
}
