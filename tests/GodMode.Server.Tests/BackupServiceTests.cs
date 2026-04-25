using GodMode.Server.Models;
using GodMode.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GodMode.Server.Tests;

public class BackupServiceTests : IDisposable
{
    private readonly string _tmp;

    public BackupServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "godmode-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { /* swallow */ }
    }

    private (BackupService svc, string rootsDir, string location) BuildService(int? retention = null)
    {
        var rootsDir = Path.Combine(_tmp, "roots");
        var location = Path.Combine(_tmp, "backups");
        Directory.CreateDirectory(rootsDir);
        Directory.CreateDirectory(location);

        var configValues = new Dictionary<string, string?>
        {
            ["ProjectRootsDir"] = rootsDir,
            ["Backup:Location"] = location,
            ["Backup:ArchivePrefix"] = "godmode-backup-",
            ["Backup:RetentionCount"] = retention?.ToString(),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var options = Options.Create(new BackupConfig
        {
            Location = location,
            ArchivePrefix = "godmode-backup-",
            RetentionCount = retention,
        });
        var logger = NullLogger<BackupService>.Instance;
        return (new BackupService(config, options, logger), rootsDir, location);
    }

    private static void SeedSampleRoots(string rootsDir)
    {
        // Profiles
        Directory.CreateDirectory(Path.Combine(rootsDir, ".profiles", "default"));
        File.WriteAllText(Path.Combine(rootsDir, ".profiles", "default", "profile.json"),
            "{\"description\":\"sample\"}");
        File.WriteAllText(Path.Combine(rootsDir, ".profiles", "default", "env.json"), "{}");

        // Webhooks
        Directory.CreateDirectory(Path.Combine(rootsDir, ".webhooks"));
        File.WriteAllText(Path.Combine(rootsDir, ".webhooks", "deploy.json"),
            "{\"token\":\"whk_abc\"}");

        // Data protection keys
        Directory.CreateDirectory(Path.Combine(rootsDir, ".godmode-keys"));
        File.WriteAllText(Path.Combine(rootsDir, ".godmode-keys", "key-1.xml"), "<key/>");

        // A project with chat history
        var projDir = Path.Combine(rootsDir, "myroot", "proj-123", ".godmode");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "status.json"), "{\"id\":\"proj-123\"}");
        File.WriteAllText(Path.Combine(projDir, "input.jsonl"), "{\"type\":\"user_input\"}\n");
        File.WriteAllText(Path.Combine(projDir, "output.jsonl"), "{\"type\":\"assistant\"}\n");
        File.WriteAllText(Path.Combine(rootsDir, "myroot", "proj-123", "README.md"), "hello");
    }

    [Fact]
    public async Task CreateBackup_WritesArchive_ToConfiguredLocation()
    {
        var (svc, rootsDir, location) = BuildService();
        SeedSampleRoots(rootsDir);

        var result = await svc.CreateBackupAsync();

        Assert.Equal(location, result.Location);
        Assert.StartsWith("godmode-backup-", result.FileName);
        Assert.EndsWith(".tar.gz", result.FileName);
        Assert.True(result.SizeBytes > 0);
        Assert.True(File.Exists(Path.Combine(location, result.FileName)));
    }

    [Fact]
    public async Task ListBackups_ReturnsArchives_NewestFirst()
    {
        var (svc, rootsDir, _) = BuildService();
        SeedSampleRoots(rootsDir);

        var first = await svc.CreateBackupAsync();
        // Filenames embed seconds; force a different one.
        await Task.Delay(1100);
        var second = await svc.CreateBackupAsync();

        var items = svc.ListBackups();
        Assert.Equal(2, items.Count);
        Assert.Equal(second.FileName, items[0].FileName);
        Assert.Equal(first.FileName, items[1].FileName);
    }

    [Fact]
    public async Task RestoreBackup_RestoresFiles_AndPreservesPreviousAside()
    {
        var (svc, rootsDir, _) = BuildService();
        SeedSampleRoots(rootsDir);
        var created = await svc.CreateBackupAsync();

        // Mutate the live directory after backup.
        File.WriteAllText(Path.Combine(rootsDir, ".profiles", "default", "profile.json"),
            "{\"description\":\"mutated\"}");
        File.WriteAllText(Path.Combine(rootsDir, "newfile.txt"), "should be moved aside");

        var result = await svc.RestoreBackupAsync(created.FileName);

        Assert.Equal(created.FileName, result.FileName);
        Assert.True(result.FilesRestored > 0);
        Assert.True(Directory.Exists(result.PreviousMovedTo));

        // Restored content matches original seed.
        var restored = File.ReadAllText(Path.Combine(rootsDir, ".profiles", "default", "profile.json"));
        Assert.Contains("\"sample\"", restored);

        // Mutated post-backup file is NOT in the live tree (it lives in PreviousMovedTo).
        Assert.False(File.Exists(Path.Combine(rootsDir, "newfile.txt")));
        Assert.True(File.Exists(Path.Combine(result.PreviousMovedTo, "newfile.txt")));

        // Project chat history round-tripped.
        Assert.True(File.Exists(Path.Combine(rootsDir, "myroot", "proj-123", ".godmode", "input.jsonl")));
        Assert.True(File.Exists(Path.Combine(rootsDir, ".godmode-keys", "key-1.xml")));
        Assert.True(File.Exists(Path.Combine(rootsDir, ".webhooks", "deploy.json")));
    }

    [Fact]
    public async Task RestoreBackup_WithNullFileName_PicksLatest()
    {
        var (svc, rootsDir, _) = BuildService();
        SeedSampleRoots(rootsDir);
        await svc.CreateBackupAsync();
        await Task.Delay(1100);
        var newest = await svc.CreateBackupAsync();

        var result = await svc.RestoreBackupAsync(null);
        Assert.Equal(newest.FileName, result.FileName);
    }

    [Fact]
    public async Task RestoreBackup_RejectsTraversalFileName()
    {
        var (svc, rootsDir, _) = BuildService();
        SeedSampleRoots(rootsDir);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.RestoreBackupAsync("../escape.tar.gz"));
    }

    [Fact]
    public async Task RestoreBackup_UnknownFileName_ThrowsFileNotFound()
    {
        var (svc, rootsDir, _) = BuildService();
        SeedSampleRoots(rootsDir);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            svc.RestoreBackupAsync("godmode-backup-9999-99-99_999999.tar.gz"));
    }

    [Fact]
    public async Task CreateBackup_RetentionCount_PrunesOlderArchives()
    {
        var (svc, rootsDir, location) = BuildService(retention: 2);
        SeedSampleRoots(rootsDir);

        await svc.CreateBackupAsync();
        await Task.Delay(1100);
        await svc.CreateBackupAsync();
        await Task.Delay(1100);
        await svc.CreateBackupAsync();

        var items = svc.ListBackups();
        Assert.Equal(2, items.Count);
        var diskCount = Directory.GetFiles(location, "godmode-backup-*.tar.gz").Length;
        Assert.Equal(2, diskCount);
    }

    [Fact]
    public void RequireLocation_WhenUnconfigured_Throws()
    {
        var rootsDir = Path.Combine(_tmp, "roots-x");
        Directory.CreateDirectory(rootsDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ProjectRootsDir"] = rootsDir })
            .Build();
        var options = Options.Create(new BackupConfig { Location = null });
        var svc = new BackupService(config, options, NullLogger<BackupService>.Instance);

        Assert.Throws<InvalidOperationException>(() => svc.RequireLocation());
    }

    [Fact]
    public async Task CreateBackup_RefusesLocationInsideRootsDir()
    {
        var rootsDir = Path.Combine(_tmp, "roots-y");
        var location = Path.Combine(rootsDir, "backups");  // inside!
        Directory.CreateDirectory(rootsDir);
        Directory.CreateDirectory(location);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectRootsDir"] = rootsDir,
                ["Backup:Location"] = location,
            }).Build();
        var options = Options.Create(new BackupConfig { Location = location });
        var svc = new BackupService(config, options, NullLogger<BackupService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateBackupAsync());
    }
}
