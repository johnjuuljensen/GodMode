using GodMode.Server.Services;

namespace GodMode.Server.Tests;

public class EnvironmentExpanderTests
{
    // --- ProfileNameToPrefix ---

    [Theory]
    [InlineData("Mega", "MEGA_")]
    [InlineData("My Profile", "MY_PROFILE_")]
    [InlineData("mega", "MEGA_")]
    [InlineData("dev-ops", "DEV_OPS_")]
    [InlineData("A.B.C", "A_B_C_")]
    [InlineData("Profile123", "PROFILE123_")]
    public void ProfileNameToPrefix_ConvertsCorrectly(string profileName, string expected)
    {
        Assert.Equal(expected, EnvironmentExpander.ProfileNameToPrefix(profileName));
    }

    [Fact]
    public void ProfileNameToPrefix_Empty_ReturnsEmpty()
    {
        Assert.Equal("", EnvironmentExpander.ProfileNameToPrefix(""));
    }

    // --- ExpandVariables ---

    [Fact]
    public void ExpandVariables_NullInput_ReturnsNull()
    {
        Assert.Null(EnvironmentExpander.ExpandVariables(null));
    }

    [Fact]
    public void ExpandVariables_EmptyInput_ReturnsEmpty()
    {
        var result = EnvironmentExpander.ExpandVariables(new Dictionary<string, string>());
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ExpandVariables_LiteralValues_PassThrough()
    {
        var env = new Dictionary<string, string>
        {
            ["KEY1"] = "value1",
            ["KEY2"] = "value2"
        };

        var result = EnvironmentExpander.ExpandVariables(env);

        Assert.NotNull(result);
        Assert.Equal("value1", result["KEY1"]);
        Assert.Equal("value2", result["KEY2"]);
    }

    [Fact]
    public void ExpandVariables_ExistingVar_Resolves()
    {
        var varName = "TEST_EXPAND_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(varName, "resolved_value");
        try
        {
            var env = new Dictionary<string, string>
            {
                ["TARGET"] = $"${{{varName}}}"
            };

            var result = EnvironmentExpander.ExpandVariables(env);

            Assert.NotNull(result);
            Assert.Equal("resolved_value", result["TARGET"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void ExpandVariables_MissingVar_SkipsEntry()
    {
        var env = new Dictionary<string, string>
        {
            ["TARGET"] = "${DEFINITELY_NONEXISTENT_VAR_12345}",
            ["KEEP"] = "literal"
        };

        var result = EnvironmentExpander.ExpandVariables(env);

        Assert.NotNull(result);
        Assert.False(result.ContainsKey("TARGET"));
        Assert.Equal("literal", result["KEEP"]);
    }

    [Fact]
    public void ExpandVariables_InlineExpansion_ResolvesWithinString()
    {
        var varName = "TEST_HOST_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(varName, "example.com");
        try
        {
            var env = new Dictionary<string, string>
            {
                ["URL"] = $"https://${{{varName}}}/api"
            };

            var result = EnvironmentExpander.ExpandVariables(env);

            Assert.NotNull(result);
            Assert.Equal("https://example.com/api", result["URL"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void ExpandVariables_InlineMissingVar_SkipsEntry()
    {
        var env = new Dictionary<string, string>
        {
            ["URL"] = "https://${NONEXISTENT_HOST_99999}/api"
        };

        var result = EnvironmentExpander.ExpandVariables(env);

        // All entries skipped → null result (no entries to return)
        Assert.True(result is null || !result.ContainsKey("URL"));
    }

    // --- GetPrefixStrippedVars ---

    [Fact]
    public void GetPrefixStrippedVars_NullProfileName_ReturnsNull()
    {
        Assert.Null(EnvironmentExpander.GetPrefixStrippedVars(null));
    }

    [Fact]
    public void GetPrefixStrippedVars_FindsPrefixedVars()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var profileName = $"Test{suffix}";
        var prefix = EnvironmentExpander.ProfileNameToPrefix(profileName);
        var fullVarName = $"{prefix}JIRA_EMAIL";

        Environment.SetEnvironmentVariable(fullVarName, "test@example.com");
        try
        {
            var result = EnvironmentExpander.GetPrefixStrippedVars(profileName);

            Assert.NotNull(result);
            Assert.True(result.ContainsKey("JIRA_EMAIL"));
            Assert.Equal("test@example.com", result["JIRA_EMAIL"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(fullVarName, null);
        }
    }

    [Fact]
    public void GetPrefixStrippedVars_NoMatchingVars_ReturnsNull()
    {
        var result = EnvironmentExpander.GetPrefixStrippedVars("UniqueProfileThatHasNoEnvVars99999");
        Assert.Null(result);
    }

    [Fact]
    public void GetPrefixStrippedVars_ExcludesControlVariable()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var profileName = $"Ctl{suffix}";
        var prefix = EnvironmentExpander.ProfileNameToPrefix(profileName);

        Environment.SetEnvironmentVariable($"{prefix}STRIP_ENV_VAR_PROFILE", "true");
        Environment.SetEnvironmentVariable($"{prefix}SOME_KEY", "value");
        try
        {
            var result = EnvironmentExpander.GetPrefixStrippedVars(profileName);

            Assert.NotNull(result);
            Assert.True(result.ContainsKey("SOME_KEY"));
            Assert.False(result.ContainsKey("STRIP_ENV_VAR_PROFILE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{prefix}STRIP_ENV_VAR_PROFILE", null);
            Environment.SetEnvironmentVariable($"{prefix}SOME_KEY", null);
        }
    }

    // --- IsStripEnabled ---

    [Fact]
    public void IsStripEnabled_ConfigFlagTrue_ReturnsTrue()
    {
        Assert.True(EnvironmentExpander.IsStripEnabled("AnyProfile", configFlag: true));
    }

    [Fact]
    public void IsStripEnabled_ConfigFlagFalse_NoEnvVar_ReturnsFalse()
    {
        Assert.False(EnvironmentExpander.IsStripEnabled("UniqueProfileNoEnv99999", configFlag: false));
    }

    [Fact]
    public void IsStripEnabled_EnvVarTrue_ReturnsTrue()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpper();
        var profileName = $"Env{suffix}";
        var prefix = EnvironmentExpander.ProfileNameToPrefix(profileName);
        var controlVar = $"{prefix}STRIP_ENV_VAR_PROFILE";

        Environment.SetEnvironmentVariable(controlVar, "true");
        try
        {
            Assert.True(EnvironmentExpander.IsStripEnabled(profileName, configFlag: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable(controlVar, null);
        }
    }

    [Fact]
    public void IsStripEnabled_NullProfileName_ReturnsFalse()
    {
        Assert.False(EnvironmentExpander.IsStripEnabled(null, configFlag: false));
    }

    // --- The server's own secrets (#279) ---

    [Theory]
    [InlineData("Authentication__ApiKey")]
    [InlineData("AUTHENTICATION__APIKEY")]
    [InlineData("authentication__apikeyfile")]
    [InlineData("Authentication:ApiKey")]
    public void IsServerSecret_TheAuthenticationSection_InAnyFormAndCase(string name) =>
        Assert.True(EnvironmentExpander.IsServerSecret(name));

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("AuthenticationKey")]
    [InlineData("MY_Authentication__ApiKey")]
    public void IsServerSecret_NotOtherVariables(string name) =>
        Assert.False(EnvironmentExpander.IsServerSecret(name));

    [Fact]
    public void ExpandVariables_AServerSecret_ExpandsEmpty_KeepsTheRest_AndIsLoggedOnce()
    {
        var secret = "Authentication__Canary" + Guid.NewGuid().ToString("N")[..8];
        var other = "TEST_OTHER_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(secret, "the-key");
        Environment.SetEnvironmentVariable(other, "other-value");
        try
        {
            var logs = new Lifecycle.CapturingLoggerProvider();
            var logger = logs.CreateLogger(nameof(EnvironmentExpander));
            var env = new Dictionary<string, string>
            {
                ["LEAK"] = $"${{{secret}}}",
                ["WRAPPED"] = $"Bearer ${{{secret}}} for ${{{other}}}",
                ["OTHER"] = $"${{{other}}}",
            };

            var result = EnvironmentExpander.ExpandVariables(env, logger);
            EnvironmentExpander.ExpandVariables(env, logger);

            Assert.NotNull(result);
            Assert.Equal("", result["LEAK"]);
            Assert.Equal("Bearer  for other-value", result["WRAPPED"]);
            Assert.Equal("other-value", result["OTHER"]);
            Assert.Single(logs.Lines, line => line.Contains(secret));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable(other, null);
        }
    }

    [Fact]
    public void ExpandVariables_AnUnsetServerSecret_StillExpandsEmpty_NotDropped()
    {
        var env = new Dictionary<string, string> { ["LEAK"] = "${Authentication__NotSet_99999}" };

        var result = EnvironmentExpander.ExpandVariables(env);

        Assert.NotNull(result);
        Assert.Equal("", result["LEAK"]);
    }

    [Fact]
    public void GetPrefixStrippedVars_LeavesOutTheServersSecrets()
    {
        // A profile named "Authentication" has the prefix AUTHENTICATION_, which Authentication__* also starts with
        var secret = "Authentication__Canary" + Guid.NewGuid().ToString("N")[..8];
        var plain = "AUTHENTICATION_PLAIN_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(secret, "the-key");
        Environment.SetEnvironmentVariable(plain, "plain-value");
        try
        {
            var result = EnvironmentExpander.GetPrefixStrippedVars("Authentication");

            Assert.NotNull(result);
            Assert.Equal("plain-value", result[plain["AUTHENTICATION_".Length..]]);
            Assert.DoesNotContain("the-key", result.Values);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable(plain, null);
        }
    }
}
