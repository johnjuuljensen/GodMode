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
}
