using System.Text.Json;
using System.Text.Json.Serialization;

namespace GodMode.TypeGen.Tests;

/// <summary>
/// What TypeGen writes for a contract, on fixture contracts of this assembly: the mappings #210 fixed
/// (string enums, nullables, <see cref="JsonElement"/>, dictionaries), and the guards that fail the
/// build rather than write a type the wire does not have.
/// </summary>
public class TypeScriptWriterTests
{
    private static readonly string Written = TypeScriptWriter.Write(typeof(IFixtureHub), typeof(IFixtureHubClient), [typeof(FixtureExtra)]);

    private static string Model(string name)
    {
        var start = Written.IndexOf($"export interface {name} {{\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No interface {name} in:\n{Written}");
        return Written[start..(Written.IndexOf("\n}\n", start, StringComparison.Ordinal) + 3)];
    }

    [Fact]
    public void StringEnum_IsAUnionOfItsNames_AsTheConverterWritesThem() =>
        Assert.Contains("export type FixtureState = 'Idle' | 'waiting-for-input';\n", Written);

    [Theory]
    [InlineData("  Name: string;\n")]
    [InlineData("  Count: number;\n")]
    [InlineData("  At: string;\n")]
    [InlineData("  State: FixtureState;\n")]
    public void NonNullable_IsRequired(string line) => Assert.Contains(line, Model("FixtureModel"));

    /// <summary>Nulls are left out of the JSON, so a nullable member is optional as well as nullable.</summary>
    [Theory]
    [InlineData("  Note?: string | null;\n")]
    [InlineData("  Limit?: number | null;\n")]
    [InlineData("  MaybeState?: FixtureState | null;\n")]
    [InlineData("  Child?: FixtureChild | null;\n")]
    public void Nullable_IsOptionalAndNull(string line) => Assert.Contains(line, Model("FixtureModel"));

    /// <summary>Arbitrary JSON is <c>unknown</c>, which takes null already: a nullable one is not <c>unknown | null</c>.</summary>
    [Theory]
    [InlineData("  Payload: unknown;\n")]
    [InlineData("  MaybePayload?: unknown;\n")]
    public void JsonElement_IsUnknown(string line) => Assert.Contains(line, Model("FixtureModel"));

    [Theory]
    [InlineData("  Counts: Record<string, number>;\n")]
    [InlineData("  Labels: Record<string, string | null>;\n")]
    [InlineData("  Children: Record<string, FixtureChild>;\n")]
    [InlineData("  MaybeCounts?: Record<string, number> | null;\n")]
    public void Dictionary_IsARecord(string line) => Assert.Contains(line, Model("FixtureModel"));

    [Theory]
    [InlineData("  Tags: (string | null)[];\n")]
    [InlineData("  States: FixtureState[];\n")]
    public void List_IsAnArray(string line) => Assert.Contains(line, Model("FixtureModel"));

    [Fact]
    public void ReachedTypes_AndExtraTypes_AreWritten()
    {
        Assert.Contains("  Value: number;\n", Model("FixtureChild"));
        Assert.Contains("  Extra: string;\n", Model("FixtureExtra"));
    }

    [Fact]
    public void Hub_ReturnsPromises_AndClientHub_ReturnsNothing()
    {
        Assert.Contains("  Get(id: string, limit?: number | null): Promise<FixtureModel>;\n", Model("IFixtureHub"));
        Assert.Contains("  Send(model: FixtureModel | null): Promise<void>;\n", Model("IFixtureHub"));
        Assert.Contains("  Find(name: string): Promise<FixtureChild | null>;\n", Model("IFixtureHub"));
        Assert.Contains("  Changed(state: FixtureState): void;\n", Model("IFixtureHubClient"));
    }

    [Fact]
    public void Documentation_IsJsDoc() =>
        Assert.Contains("  /** The fixture's name. */\n  Name: string;\n", Model("FixtureModel"));

    [Theory]
    [InlineData(typeof(IUnmappedGenericHub), "HashSet")]
    [InlineData(typeof(IUnmappedScalarHub), "System.Uri")]
    [InlineData(typeof(IUnmappedMemberHub), "System.Version")]
    public void UnmappedType_FailsTheWrite(Type hub, string named)
    {
        var ex = Assert.Throws<NotSupportedException>(() => TypeScriptWriter.Write(hub, typeof(IFixtureHubClient), []));
        Assert.StartsWith("No TypeScript mapping for ", ex.Message);
        Assert.Contains(named, ex.Message);
    }

    [Fact]
    public void EnumWithoutStringConverter_FailsTheWrite() =>
        Assert.Contains(nameof(NumberEnum),
            Assert.Throws<NotSupportedException>(() => TypeScriptWriter.Write(typeof(INumberEnumHub), typeof(IFixtureHubClient), [])).Message);

    [Fact]
    public void OverloadedHubMethod_FailsTheWrite() =>
        Assert.Contains("is overloaded",
            Assert.Throws<NotSupportedException>(() => TypeScriptWriter.Write(typeof(IOverloadedHub), typeof(IFixtureHubClient), [])).Message);
}

public interface IFixtureHub
{
    Task<FixtureModel> Get(string id, int? limit = null);
    Task Send(FixtureModel? model);
    Task<FixtureChild?> Find(string name);
}

public interface IFixtureHubClient
{
    Task Changed(FixtureState state);
}

[JsonConverter(typeof(JsonStringEnumConverter<FixtureState>))]
public enum FixtureState
{
    Idle,
    [JsonStringEnumMemberName("waiting-for-input")] WaitingForInput,
}

public class FixtureModel
{
    /// <summary>The fixture's name.</summary>
    public string Name { get; set; } = "";
    public string? Note { get; set; }
    public int Count { get; set; }
    public int? Limit { get; set; }
    public DateTime At { get; set; }
    public FixtureState State { get; set; }
    public FixtureState? MaybeState { get; set; }
    public FixtureChild? Child { get; set; }
    public JsonElement Payload { get; set; }
    public JsonElement? MaybePayload { get; set; }
    public Dictionary<string, int> Counts { get; set; } = [];
    public IReadOnlyDictionary<string, string?> Labels { get; set; } = new Dictionary<string, string?>();
    public Dictionary<string, FixtureChild> Children { get; set; } = [];
    public Dictionary<string, int>? MaybeCounts { get; set; }
    public List<string?> Tags { get; set; } = [];
    public FixtureState[] States { get; set; } = [];
}

public record FixtureChild(int Value);

public record FixtureExtra(string Extra);

public interface IUnmappedGenericHub
{
    Task Take(HashSet<int> set);
}

public interface IUnmappedScalarHub
{
    Task Take(Uri uri);
}

public interface IUnmappedMemberHub
{
    Task Take(WithUnmappedMember model);
}

public record WithUnmappedMember(Version Version);

public enum NumberEnum { One, Two }

public interface INumberEnumHub
{
    Task Take(NumberEnum value);
}

public interface IOverloadedHub
{
    Task Take(string value);
    Task Take(int value);
}
