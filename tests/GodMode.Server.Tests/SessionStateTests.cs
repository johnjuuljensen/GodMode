using GodMode.ProjectFiles;

namespace GodMode.Server.Tests;

/// <summary>A session's id, <c>yymmdd-{kind}-{slug}-{suffix}</c>: short, lowercase <c>[a-z0-9-]</c>, and random at its end.</summary>
public class SessionStateTests
{
    private static readonly DateTime Day = new(2026, 9, 29, 23, 59, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("feat", "Left list", "abcd", "260929-feat-left-list-abcd")]
    [InlineData("Bug", "Crash: on START!", "k7q2", "260929-bug-crash-on-start-k7q2")]
    [InlineData("Create", "Æblegrød på én gang", "2345", "260929-create-aeblegroed-paa-en-gang-2345")]
    [InlineData("feat", "...", "abcd", "260929-feat-abcd")]
    [InlineData("", "x", "abcd", "260929-session-x-abcd")]
    [InlineData("!!!", "x", "abcd", "260929-session-x-abcd")]
    public void Id_IsTheDate_Kind_Slug_AndSuffix(string kind, string name, string suffix, string id)
    {
        Assert.Equal(id, SessionState.Id(Day, kind, name, suffix));
        Assert.True(SessionState.IsId(id));
    }

    /// <summary>Windows paths are short: the slug is cut at 24 characters, and a kind at 12, with no dash left at the end.</summary>
    [Fact]
    public void SlugAndKind_AreCutShort()
    {
        var id = SessionState.Id(Day, "a-very-long-kind-name", "The quick brown fox jumps over the lazy dog", "abcd");

        Assert.Equal("260929-a-very-long-the-quick-brown-fox-jump-abcd", id);
        Assert.True(id.Length <= 6 + 1 + SessionState.MaxKindLength + 1 + SessionState.MaxSlugLength + 1 + SessionState.SuffixLength);
    }

    [Fact]
    public void Suffix_IsFourBase32Characters_AndRandom()
    {
        var suffixes = Enumerable.Range(0, 50).Select(_ => SessionState.NewSuffix()).ToArray();

        Assert.All(suffixes, suffix => Assert.Matches("^[a-z2-7]{4}$", suffix));
        Assert.True(suffixes.Distinct().Count() > 40, "the suffixes are not random");
    }

    [Theory]
    [InlineData("260929-feat-left-list-k7q2", true)]
    [InlineData("260929-feat-k7q2", true)]
    [InlineData("260929-feat", false)]
    [InlineData("260929-feat-left-list-K7Q2", false)]
    [InlineData("260929-feat-left-list-k7q1", false)]
    [InlineData("26929-feat-x-k7q2", false)]
    [InlineData("260929--x-k7q2", false)]
    [InlineData("../260929-feat-x-k7q2", false)]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", false)]
    [InlineData("", false)]
    public void IsId_TakesOnlyIdsInTheirForm(string id, bool valid) => Assert.Equal(valid, SessionState.IsId(id));

    /// <summary>The sessions in a folder are its <c>.godmode/sessions/{id}/</c> with a status.json; a flat <c>.godmode/status.json</c> is none.</summary>
    [Fact]
    public void List_FindsSessionsFolders_AndNotTheFlatLayout()
    {
        var folder = ServerProcess.CreateWorkDir("sessions");
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, ".godmode"));
            File.WriteAllText(Path.Combine(folder, ".godmode", "status.json"), "{}");
            Assert.Empty(SessionState.List(folder));

            var state = SessionState.Create(folder, "260929-feat-x-abcd");
            Assert.Empty(SessionState.List(folder));
            File.WriteAllText(Path.Combine(state, "status.json"), "{}");
            Directory.CreateDirectory(Path.Combine(SessionState.SessionsPathOf(folder), "junk"));
            File.WriteAllText(Path.Combine(SessionState.SessionsPathOf(folder), "junk", "status.json"), "{}");

            Assert.Equal(["260929-feat-x-abcd"], SessionState.List(folder));
            Assert.Equal(Path.Combine(folder, ".godmode", "sessions", "260929-feat-x-abcd"), state);
            Assert.True(File.Exists(Path.Combine(folder, ".godmode", ".gitignore")));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(folder);
        }
    }
    /// <summary>
    /// A trashed session is no session of its folder (<see cref="SessionState.List"/>), but keeps its id
    /// in its root (<see cref="ProjectManager.HasSession"/>) for its undo, which puts it back whole.
    /// </summary>
    [Fact]
    public void Trash_TakesTheSessionOutOfTheFolder_KeepsItsId_AndRestorePutsItBack()
    {
        var root = ServerProcess.CreateWorkDir("trash");
        try
        {
            var folder = Path.Combine(root, "workspace");
            const string id = "260929-chat-x-abcd";
            var state = SessionState.Create(folder, id);
            File.WriteAllText(Path.Combine(state, "status.json"), "{}");
            var files = new ProjectManager(new Dictionary<string, string> { ["p/r"] = root });

            Assert.True(SessionState.Trash(folder, id, DateTime.UtcNow));

            Assert.Empty(SessionState.List(folder));
            Assert.Empty(files.ListSessions("p/r"));
            Assert.Equal([(folder, id)], files.ListTrashed("p/r"));
            Assert.True(files.HasSession("p/r", id), "a trashed session's id is free for a new session");
            Assert.False(SessionState.Trash(folder, id, DateTime.UtcNow), "a session with no state was trashed");

            SessionState.Restore(folder, id);

            Assert.Equal([id], SessionState.List(folder));
            Assert.Empty(SessionState.ListTrashed(folder));
            Assert.False(File.Exists(Path.Combine(state, SessionState.TrashedAtFileName)));
            Assert.Throws<DirectoryNotFoundException>(() => SessionState.Restore(folder, id));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(root);
        }
    }

    /// <summary>A shared working folder is made when missing and used when there: two creates into one new folder at once both have it.</summary>
    [Fact]
    public void CreateShared_MakesTheFolder_OrUsesItWhenItIsThere()
    {
        var root = ServerProcess.CreateWorkDir("shared");
        try
        {
            Assert.True(ProjectFolder.CreateShared(root, "workspace").Made);
            Assert.False(ProjectFolder.CreateShared(root, "workspace").Made);
            Assert.Throws<IOException>(() => ProjectFolder.Create(root, "workspace"));
        }
        finally
        {
            ServerProcess.DeleteWorkDir(root);
        }
    }
}
