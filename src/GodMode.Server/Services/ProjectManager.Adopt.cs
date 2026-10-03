using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using ProjectFiles = GodMode.ProjectFiles;

namespace GodMode.Server.Services;

// Adopting folders that exist in a root (#370): which folders a root offers, making a session of one, and
// forgetting a session while its folder stays
public partial class ProjectManager
{
    public const string ListScriptTimeoutSetting = "ListScriptTimeoutSeconds";

    /// <summary>What an adopt tells an action's create script (<c>"adopt": true</c>): it is adopting, and makes nothing.</summary>
    public const string AdoptVariable = "GODMODE_ADOPT";

    private TimeSpan ListScriptTimeout => TimeSpan.FromSeconds(_configuration.GetValue(ListScriptTimeoutSetting, 30.0));

    public async Task<UnmanagedFolder[]> ListUnmanagedAsync(string profileName, string rootName)
    {
        var snap = _snapshot;
        var (rootPath, config) = ReadRootForAdopt(snap, profileName, rootName);

        IReadOnlyList<UnmanagedFolder> candidates;
        if (config.List is { } script)
        {
            snap.Profiles.TryGetValue(profileName, out var profile);
            // config.json's own environment: the listing is the root's, no action's
            var env = BuildScriptEnvironment(rootPath, null, new CreateAction("list", Environment: config.Environment), new Dictionary<string, JsonElement>(),
                profile?.Environment, profileName: profileName, stripEnvVarProfile: config.StripEnvVarProfile);
            string output;
            using (var timeout = new CancellationTokenSource(ListScriptTimeout))
            {
                try
                {
                    output = await _scriptRunner.RunForOutputAsync(script, rootPath, rootPath, env, UnmanagedList.MaxOutputChars, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"Root '{rootName}''s list script {script} took longer than {ListScriptTimeout.TotalSeconds}s.");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Root '{rootName}''s list script {script} failed: {ex.Message}", ex);
                }
            }
            try
            {
                candidates = UnmanagedList.Parse(output, rootPath, config, WhyNotAnAdoptableFolder, path => WhyNeverOffered(path) != null);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"Root '{rootName}''s list script {script} printed what is not the list: {ex.Message}", ex);
            }
        }
        else
        {
            candidates = DefaultCandidates(rootPath);
        }

        // Nothing a session works in is offered, nor a folder a create is making
        return candidates.Where(candidate => WhyFolderHasASession(Path.Combine(rootPath, candidate.Path)) == null).ToArray();
    }

    /// <summary>
    /// The issue as the root's <c>issueInfo</c> script reports it, run now in the root with config.json's own
    /// environment and the issue in <c>GODMODE_INPUT_ISSUE</c>; null when the root has none (#473).
    /// </summary>
    public async Task<IssueInfo?> DescribeIssueAsync(string profileName, string rootName, string issue)
    {
        if (string.IsNullOrWhiteSpace(issue) || issue.Length > 100 || issue.Any(char.IsControl))
            throw new ArgumentException("The issue must be a number or key of at most 100 characters.");
        var snap = _snapshot;
        var (rootPath, config) = ReadRootForAdopt(snap, profileName, rootName);
        if (config.IssueInfo is not { } script)
            return null;

        snap.Profiles.TryGetValue(profileName, out var profile);
        var env = BuildScriptEnvironment(rootPath, null, new CreateAction("issueInfo", Environment: config.Environment),
            new Dictionary<string, JsonElement> { ["issue"] = JsonSerializer.SerializeToElement(issue.Trim()) },
            profile?.Environment, profileName: profileName, stripEnvVarProfile: config.StripEnvVarProfile);
        string output;
        using (var timeout = new CancellationTokenSource(ListScriptTimeout))
        {
            try
            {
                output = await _scriptRunner.RunForOutputAsync(script, rootPath, rootPath, env, IssueInfoScript.MaxOutputChars, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException($"Root '{rootName}''s issueInfo script {script} took longer than {ListScriptTimeout.TotalSeconds}s.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Root '{rootName}''s issueInfo script {script} failed: {ex.Message}", ex);
            }
        }
        try
        {
            return IssueInfoScript.Parse(output);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Root '{rootName}''s issueInfo script {script} printed what is not an issue: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The folders a root without a list script offers: its immediate subfolders, in ordinal order, but its
    /// own (<c>.godmode-root</c>, <c>logs</c>…), the hidden ones (a name that starts with <c>.</c>), and any
    /// that is no project folder of it (a link out of it, a name Windows would change). None when the root
    /// is its own workspace: its folders are then its repo's.
    /// </summary>
    private IReadOnlyList<UnmanagedFolder> DefaultCandidates(string rootPath)
    {
        if (!Directory.Exists(rootPath) || WhyRootIsAWorkspace(rootPath) != null) return [];
        return Directory.GetDirectories(rootPath)
            .Select(folder => Path.GetFileName(folder))
            .Where(name => WhyNotAnAdoptableFolder(rootPath, Path.Combine(rootPath, name)) == null)
            .Order(StringComparer.Ordinal)
            .Select(name => new UnmanagedFolder(name, name))
            .ToArray();
    }

    /// <summary>
    /// Why <paramref name="path"/> cannot be adopted as a working folder of the root at <paramref name="rootPath"/>,
    /// or null when it can: it must be a folder that exists, directly in the root (recovery finds a session
    /// only there), a project folder of it (<see cref="WhyNotAProjectFolderOf"/>, links followed), and named
    /// as a folder of its own (<see cref="ProjectFiles.ProjectFolder.ValidateFolderName"/>).
    /// </summary>
    private static string? WhyNotAnAdoptableFolder(string rootPath, string path)
    {
        var full = FullPath(path);
        if (!PathComparer.Equals(Path.GetDirectoryName(full), FullPath(rootPath)))
            return "is not a folder directly in its root";
        if (WhyNeverOffered(full) is { } never)
            return never;
        if (WhyNotAProjectFolderOf(rootPath, full) is { } reason)
            return reason;
        try
        {
            ProjectFiles.ProjectFolder.ValidateFolderName(Path.GetFileName(full), "path");
        }
        catch (ArgumentException ex)
        {
            return $"is no folder of its own: {ex.Message}";
        }
        return Directory.Exists(full) ? null : "does not exist, and an adopt makes no folder";
    }

    /// <summary>
    /// Why the folder at <paramref name="path"/> is never offered or adopted, whatever a list script says, or
    /// null: a hidden one (a name that starts with <c>.</c>), which is the root's machinery and not a working
    /// folder (<c>.bare</c>, the bare repository every worktree of the root shares, whose delete would take
    /// them all), or a root of its own (it holds a <c>.godmode-root</c>).
    /// </summary>
    private static string? WhyNeverOffered(string path) =>
        Path.GetFileName(path).StartsWith('.') ? "is a hidden folder (its name starts with '.'), which is never adopted"
        : Directory.Exists(Path.Combine(path, ProjectFiles.ProjectFolder.RootConfigFolderName)) ? $"is a root of its own (it has a {ProjectFiles.ProjectFolder.RootConfigFolderName}), which is never adopted"
        : null;

    /// <summary>
    /// Why the folder at <paramref name="path"/> is GodMode's already, or null when it is not: a tracked
    /// session works in it, a session has its state in its <c>.godmode/sessions/</c>, or a create is making it.
    /// A session in its trash (deleted or forgotten) does not count: the folder is no session's.
    /// </summary>
    private string? WhyFolderHasASession(string path)
    {
        var full = FullPath(path);
        return _projects.Values.FirstOrDefault(p => PathComparer.Equals(FullPath(p.ProjectPath), full)) is { } tracked ? $"session {tracked.Status.Id} works in it"
            : ProjectFiles.SessionState.List(full).FirstOrDefault() is { } onDisk ? $"session {onDisk} has its state in it"
            : IsClaimedByCreate(full) ? "a create is making a session in it"
            : null;
    }

    /// <summary>
    /// The root's full path and its config, read strictly: a config that cannot be read fails, rather than
    /// reading as the default, whose listing and adopt are not the root's.
    /// </summary>
    private (string RootPath, RootConfig Config) ReadRootForAdopt(ProfileSnapshot snap, string profileName, string rootName)
    {
        string rootPath;
        try
        {
            rootPath = FullPath(snap.ProjectFiles.GetProjectRootPath(CompositeKey(profileName, rootName)));
        }
        catch (KeyNotFoundException)
        {
            throw new KeyNotFoundException($"This server lists no root '{rootName}' in profile '{profileName}'.");
        }
        try
        {
            return (rootPath, _rootConfigReader.ReadConfigStrict(rootPath));
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Root '{rootName}' has a config that cannot be read: {ex.Message}", ex);
        }
    }

    public async Task<ProjectStatus> AdoptFolderAsync(string profileName, string rootName, string path, string? actionName,
        Dictionary<string, JsonElement>? inputs)
    {
        try { return await AdoptFolderCoreAsync(profileName, rootName, path, actionName, inputs ?? new Dictionary<string, JsonElement>()); }
        finally { await PushAttentionIfChangedAsync(); }
    }

    /// <summary>
    /// An adopt: a session of a folder that exists, made as a create makes one but for the folder, which is
    /// used as it is, and the scripts: no prepare script runs, and the create script only when the action
    /// says it adopts (<see cref="CreateAction.Adopt"/>), with <see cref="AdoptVariable"/>. Anything that
    /// fails before the launch changes nothing: no session is left, in Error or otherwise, since a delete
    /// of one would take the folder with it.
    /// </summary>
    private async Task<ProjectStatus> AdoptFolderCoreAsync(string requestedProfile, string requestedRoot, string path, string? actionName,
        Dictionary<string, JsonElement> inputs)
    {
        var snap = _snapshot;
        var (rootPath, config) = ReadRootForAdopt(snap, requestedProfile, requestedRoot);
        var action = config.ResolveAction(actionName)
            ?? throw new ArgumentException($"Action '{actionName}' not found in root '{requestedRoot}'.");
        if (!action.Session)
            throw new ArgumentException($"Action '{action.Name}' of root '{requestedRoot}' starts no session, so it adopts no folder.");
        RequestedEffort(inputs);

        var projectPath = FullPath(Path.Combine(rootPath, path));
        if (WhyNotAnAdoptableFolder(rootPath, projectPath) is { } notAdoptable)
            throw new ArgumentException($"'{path}' {notAdoptable}.");
        if (WhyFolderHasASession(projectPath) is { } inUse)
            throw new ProjectInUseException(projectPath, inUse);
        var folder = Path.GetFileName(projectPath);

        var compositeKey = CompositeKey(requestedProfile, requestedRoot);
        var (profileName, rootName) = ConfiguredNames(snap, requestedProfile, requestedRoot);
        var name = TemplateResolver.GetString(inputs, "name") is { Length: > 0 } given && !string.IsNullOrWhiteSpace(given) ? given : folder;
        var kind = ProjectFiles.SessionState.Kind(TemplateResolver.GetString(inputs, "kind") ?? action.Name);
        var prompt = TemplateResolver.GetString(inputs, "prompt");
        var createdOn = DateTime.Now;
        var sessionId = FreeSessionId(snap, compositeKey, profileName, rootName,
            suffix => ProjectFiles.SessionState.Id(createdOn, kind, name, suffix));
        var projectId = ProjectId(profileName, rootName, sessionId);

        // As a create claims its folder: no other create or session may come into it meanwhile
        using var claims = new CreateClaims(this, action.SharedFolder, rootPath);
        claims.Claim(projectId, projectPath);
        _logger.LogInformation("Adopting {Folder} in profile '{Profile}' root '{Root}' as {ProjectId}, action '{Action}' ({How})",
            projectPath, profileName, rootName, projectId, action.Name, action.Adopt && action.Create is { Length: > 0 } ? "its create script adopts" : "no script runs");

        var now = DateTime.UtcNow;
        var project = new ProjectInfo
        {
            Status = new ProjectStatus(projectId, name, ProjectState.Running, now, now, CurrentQuestion: null,
                new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0), Git: null, Tests: null, OutputOffset: 0,
                RootName: rootName, ProfileName: profileName, Kind: kind, ActionName: action.Name, SharedFolder: action.SharedFolder, Adopted: true),
            ProjectPath = projectPath,
            RootPath = rootPath,
            SessionId = sessionId,
            ActionName = action.Name,
            ProfileName = profileName,
            SharedFolder = action.SharedFolder,
        };

        // Only an action that says it adopts runs a script, its create script alone, which must make nothing
        if (action.Adopt && action.Create is { Length: > 0 })
        {
            var resultFilePath = GetResultFilePath(rootPath, sessionId);
            if (File.Exists(resultFilePath)) File.Delete(resultFilePath);
            snap.Profiles.TryGetValue(profileName, out var profile);
            var scriptEnv = BuildScriptEnvironment(rootPath, project, action, inputs, profile?.Environment, resultFilePath,
                profileName, config.StripEnvVarProfile);
            scriptEnv[AdoptVariable] = "true";
            var logFilePath = GetScriptLogPath(rootPath, sessionId);
            try
            {
                await _scriptRunner.RunAsync(action.Create, rootPath, projectPath, scriptEnv,
                    msg => _hubContext.Clients.All.CreationProgress(projectId, msg), logFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adopt script failed for {ProjectId}, so {Folder} is not adopted. See log: {LogPath}", projectId, projectPath, logFilePath);
                throw new InvalidOperationException($"The adopt script of action '{action.Name}' failed, and '{folder}' is not adopted: {ex.Message}", ex);
            }

            var results = ReadResultFile(resultFilePath, _logger);
            // The folder is the one adopted: an adopt script names no other
            if (results.TryGetValue("project_path", out var otherPath) && !string.IsNullOrWhiteSpace(otherPath)
                && !PathComparer.Equals(FullPath(otherPath), projectPath))
                _logger.LogWarning("Adopt script for {ProjectId} named project_path {Path}, which is ignored: the session is {Folder}'s", projectId, otherPath, projectPath);
            if (results.TryGetValue("project_name", out var scriptName) && !string.IsNullOrWhiteSpace(scriptName)) name = scriptName;
            if (results.TryGetValue("kind", out var scriptKind) && !string.IsNullOrWhiteSpace(scriptKind)) kind = ProjectFiles.SessionState.Kind(scriptKind);
            if (results.TryGetValue("project_prompt", out var scriptPrompt) && !string.IsNullOrWhiteSpace(scriptPrompt)) prompt = scriptPrompt;
            projectId = FinalizeSessionId(snap, claims, project, compositeKey, profileName, rootName, rootPath, createdOn, kind, name, logFilePath, resultFilePath);
        }

        var settings = new ProjectFiles.ProjectSettings(
            ActionName: action.Name,
            PermissionMode: action.PermissionMode,
            SharedFolder: action.SharedFolder,
            Adopted: true,
            Importance: action.Importance);
        return await LaunchNewSessionAsync(project, action, inputs, name, kind, prompt, settings);
    }

    public async Task<DeleteProjectResult> ForgetProjectAsync(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        return await WithTrackedLockAsync(project, () => ForgetLockedAsync(project));
    }

    /// <summary>
    /// A forget: the session is stopped and leaves the list, no delete script runs, and only its state
    /// moves, to its folder's trash, marked forgotten, so a restore brings it back as it was. Its folder,
    /// and every other file in it, stays.
    /// </summary>
    private async Task<DeleteProjectResult> ForgetLockedAsync(ProjectInfo project)
    {
        var projectId = project.Status.Id;
        _logger.LogInformation("Forgetting project {ProjectId} ({Name}); its folder {Folder} stays", projectId, project.Status.Name, project.ProjectPath);

        await _lifecycle.StopAsync(project);
        await _lifecycle.UpdateStatusAsync(project, status => status with { CurrentQuestion = null });
        await NotifyStatusChanged(project);
        await _pullRequests.ForgetAsync(projectId);

        _projects.TryRemove(projectId, out _);
        ForgetFleetGrant(project);
        await project.Process.CloseAsync();
        await _pullRequests.ForgetAsync(projectId);

        var trashed = await TrashSessionStateAsync(project, forgotten: true);
        _logger.LogInformation("Project {ProjectId} forgotten{Trashed}", projectId, trashed ? ", with its state in the trash" : "; it had no state");
        await PushAttentionIfChangedAsync();
        return new DeleteProjectResult(trashed);
    }
}
