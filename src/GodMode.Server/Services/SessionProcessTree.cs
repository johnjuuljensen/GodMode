using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// A session's claude and every process it starts, held together so that a stop reaches all of them
/// and leaves none behind. On Windows it is a Job Object, and claude has a hidden console of its own;
/// on Linux it is a session and process group of its own (claude is started through <c>setsid</c>).
/// Either way the session is off the server's console: a Ctrl+C in the server's terminal reaches the
/// server alone, which then stops its sessions itself.
/// <para>
/// A stop is <see cref="InterruptAsync"/>, which claude answers by ending its turn and exiting, then,
/// if it has not exited in time, <see cref="Kill"/>, which takes the whole tree, a child that
/// re-parented included. The interrupt is the one claude honours whatever the server was started
/// from: on Windows, Ctrl+Break raised in the session's own console (a Ctrl+C there is lost when the
/// server was started with Ctrl+C ignored, which its children inherit); elsewhere SIGINT to the group.
/// When the session's exit has been handled the tree is disposed, and whatever is left of it goes too.
/// </para>
/// <para>
/// A server that dies without stopping its sessions (a crash, a SIGKILL) takes a Job Object with it, and
/// so its sessions. A process group outlives it: on Linux each group is recorded in the root while it runs,
/// and the next server to hold the root reaps what is left of it before it recovers the root's sessions
/// (<see cref="ReapOrphansAsync"/>). macOS has neither, and a crash there leaves claude running.
/// </para>
/// </summary>
internal abstract class SessionProcessTree : IDisposable
{
    /// <summary>
    /// The argument that makes GodMode.Server this helper instead of the server, on Windows: it raises
    /// Ctrl+Break in the console of the process whose id follows. A process can only raise a console
    /// event in its own console, and the server cannot leave its own for a moment to do it.
    /// </summary>
    public const string ConsoleBreakFlag = "--godmode-console-break";

    private readonly object _gate = new();
    private bool _disposed;

    protected ILogger Logger { get; }

    protected SessionProcessTree(ILogger logger) => Logger = logger;

    /// <summary>
    /// The tree this platform gives a session: see the class summary. On Linux its group is recorded at
    /// <paramref name="recordPath"/> (<see cref="RecordPathFor"/>) while it runs, for the next server to
    /// reap should this one die without stopping it (<see cref="ReapOrphansAsync"/>).
    /// </summary>
    public static SessionProcessTree Create(ILogger logger, string? recordPath = null) =>
        OperatingSystem.IsWindows() ? new JobObjectTree(logger)
        : Setsid.Value is { } setsid ? new ProcessGroupTree(setsid, recordPath, logger)
        : new ProcessOnlyTree(logger);

    /// <summary>Makes the start put claude in the tree. Before <see cref="Process.Start()"/>.</summary>
    public abstract void Prepare(ProcessStartInfo startInfo);

    /// <summary>Takes the started process into the tree.</summary>
    public abstract void Attach(Process process);

    /// <summary>Asks claude to end its turn and exit. Returns once it has been asked.</summary>
    public abstract Task InterruptAsync();

    /// <summary>Kills every process in the tree at once. Nothing once the tree is disposed.</summary>
    public void Kill()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { KillTree(); }
            catch (Exception ex) { Logger.LogWarning(ex, "Could not kill a session's processes"); }
        }
    }

    protected abstract void KillTree();

    /// <summary>The session's exit has been handled: whatever is left of its tree is killed with it.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { Release(); }
            catch (Exception ex) { Logger.LogWarning(ex, "Could not release a session's processes"); }
        }
    }

    protected abstract void Release();

    // ── Windows: a Job Object, and a console of its own ──

    private sealed class JobObjectTree(ILogger logger) : SessionProcessTree(logger)
    {
        private IntPtr _job;
        private Process? _process;

        public override void Prepare(ProcessStartInfo startInfo) =>
            // A console of its own, without a window: not the server's
            startInfo.CreateNoWindow = true;

        public override void Attach(Process process)
        {
            _process = process;
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            try
            {
                // Closing the last handle kills what is left: when the session's exit is handled, and
                // should the server itself die
                var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>())
                    || !AssignProcessToJobObject(job, process.Handle))
                    throw new Win32Exception();
            }
            catch
            {
                CloseHandle(job);
                throw;
            }
            _job = job;
        }

        public override async Task InterruptAsync()
        {
            if (_process is not { } process) return;
            var helper = Path.Combine(AppContext.BaseDirectory, "GodMode.Server.exe");
            if (!File.Exists(helper))
            {
                Logger.LogWarning("{Helper} is missing, so a stop cannot interrupt claude; it closes its input, then kills it", helper);
                return;
            }

            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(ConsoleBreakFlag);
            start.ArgumentList.Add(process.Id.ToString());
            start.Environment.Clear();
            foreach (var (key, value) in ChildEnvironment.Script.Build(ChildEnvironment.Current(), null))
                start.Environment[key] = value;

            using var raising = Process.Start(start)!;
            var error = raising.StandardError.ReadToEndAsync();
            _ = raising.StandardOutput.ReadToEndAsync();
            await raising.WaitForExitAsync();
            if (raising.ExitCode != 0)
                Logger.LogWarning("Could not raise Ctrl+Break in the console of claude (PID {ProcessId}): {Error}", process.Id, (await error).Trim());
        }

        protected override void KillTree()
        {
            if (_job != IntPtr.Zero)
            {
                if (!TerminateJobObject(_job, 1)) throw new Win32Exception();
            }
            else if (_process is { HasExited: false } process)
                process.Kill(entireProcessTree: true);
        }

        protected override void Release()
        {
            if (_job == IntPtr.Zero) return;
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        private const int JobObjectExtendedLimitInformationClass = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>
    /// GodMode.Server started with <see cref="ConsoleBreakFlag"/>: raises Ctrl+Break in the console
    /// of the process whose id follows, and exits 0 if it did.
    /// </summary>
    public static int RunConsoleBreakHelper(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args is not [ConsoleBreakFlag, var pidText] || !uint.TryParse(pidText, out var pid))
        {
            Console.Error.WriteLine($"usage (Windows): {ConsoleBreakFlag} <pid>");
            return 2;
        }

        FreeConsole();
        if (!AttachConsole(pid))
        {
            Console.Error.WriteLine($"Cannot attach to the console of process {pid}: {new Win32Exception().Message}");
            return 1;
        }
        // This process is on that console too: it lets the event pass it by
        SetConsoleCtrlHandler(IgnoreConsoleEvent, true);
        var raised = GenerateConsoleCtrlEvent(CtrlBreakEvent, 0);
        var error = raised ? null : new Win32Exception().Message;
        // The console runs the handlers on a thread of its own: leaving before it has run here would
        // end this process as the event's casualty
        if (raised) ConsoleEventPassed.Wait(TimeSpan.FromSeconds(5));
        FreeConsole();
        if (error != null) Console.Error.WriteLine($"Cannot raise Ctrl+Break in the console of process {pid}: {error}");
        return raised ? 0 : 1;
    }

    private const uint CtrlBreakEvent = 1;

    private delegate bool ConsoleEventHandler(uint eventType);

    /// <summary>Set once the event has reached the helper too, so it has reached every process on the console.</summary>
    private static readonly ManualResetEventSlim ConsoleEventPassed = new();

    /// <summary>Kept in a field: the console calls it after the call that registered it returns.</summary>
    private static readonly ConsoleEventHandler IgnoreConsoleEvent = _ =>
    {
        ConsoleEventPassed.Set();
        return true;
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleEventHandler handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    // ── Linux: a session and process group of its own ──

    /// <summary><c>setsid</c> on PATH, which starts a program as the leader of a new session and process group; null where there is none (macOS).</summary>
    private static readonly Lazy<string?> Setsid = new(() => FindOnPath("setsid", Environment.GetEnvironmentVariable("PATH")));

    private sealed class ProcessGroupTree(string setsid, string? recordPath, ILogger logger) : SessionProcessTree(logger)
    {
        private Process? _process;
        private int _group;
        /// <summary>This launch's mark, in claude's environment (<see cref="LaunchVariable"/>) and in its record.</summary>
        private readonly string _launch = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Runs claude through <c>setsid</c>, which makes it the leader of a new session, in the same
        /// process: its pid is its group's id. claude is found on PATH first, so a missing one fails
        /// the start as it does on Windows rather than as an exit of <c>setsid</c>. Its environment
        /// carries the launch's mark, which every process it starts inherits.
        /// </summary>
        public override void Prepare(ProcessStartInfo startInfo)
        {
            startInfo.Environment[LaunchVariable] = _launch;
            var path = startInfo.Environment.TryGetValue("PATH", out var childPath) ? childPath : Environment.GetEnvironmentVariable("PATH");
            var executable = startInfo.FileName.Contains('/')
                ? Path.GetFullPath(startInfo.FileName)
                : FindOnPath(startInfo.FileName, path);
            if (executable == null || !File.Exists(executable))
                throw new FileNotFoundException($"Cannot start '{startInfo.FileName}': no such executable on PATH", startInfo.FileName);
            startInfo.ArgumentList.Insert(0, executable);
            startInfo.FileName = setsid;
        }

        public override void Attach(Process process)
        {
            _process = process;
            _group = process.Id;
            if (recordPath == null) return;
            LiveLaunches[_launch] = 0;
            try
            {
                // Whole or not at all: a reap that read half of it would take it for no record, and delete it
                Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
                var writing = recordPath + ".tmp";
                File.WriteAllText(writing, JsonSerializer.Serialize(new GroupRecord(_group, _launch)));
                File.Move(writing, recordPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Could not record the process group of claude (PID {ProcessId}) at {Path}: a crash of the server would leave it running", _group, recordPath);
            }
        }

        public override Task InterruptAsync()
        {
            Signal(-_group, SigInt);
            return Task.CompletedTask;
        }

        /// <summary>
        /// A descendant that left the group (setsid, setpgid: a detached spawn, a daemon) escapes the
        /// group's kill, so while claude runs its descendants are walked and killed first, before the
        /// group's kill takes the parents they are found through. Then the group, which holds any that
        /// re-parented.
        /// </summary>
        protected override void KillTree()
        {
            try
            {
                if (_process is { HasExited: false } process) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                Logger.LogDebug(ex, "Could not walk the process tree of claude (PID {ProcessId})", _group);
            }
            Signal(-_group, SigKill);
        }

        /// <summary>Kills what is left of the group, then forgets its record, unless a later launch of the session has written its own.</summary>
        protected override void Release()
        {
            Signal(-_group, SigKill);
            if (recordPath == null || !LiveLaunches.TryRemove(_launch, out _)) return;
            if (ReadRecord(recordPath) is { } record && record.Launch == _launch) DeleteRecord(recordPath, Logger);
        }
    }

    // ── Linux: the groups a server that died left running ──

    /// <summary>
    /// The variable a session's claude, and every process it starts, carries its launch's mark in: what
    /// tells a group the next server finds from one that took its id since (<see cref="ReapOrphansAsync"/>).
    /// </summary>
    public const string LaunchVariable = "GODMODE_LAUNCH";

    /// <summary>How long the reap waits for an orphan to end on SIGTERM before it kills what is left.</summary>
    public static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromSeconds(3);

    private const string RecordSuffix = ".process-group.json";

    /// <summary>Where a session's group is recorded while its claude runs: <c>{root}/logs/{id}.process-group.json</c>.</summary>
    public static string RecordPathFor(string rootPath, string sessionId) =>
        Path.Combine(rootPath, GodMode.ProjectFiles.ProjectFolder.ScriptLogsFolderName, sessionId + RecordSuffix);

    /// <summary>A running claude's process group, and its launch's mark (<see cref="LaunchVariable"/>).</summary>
    internal sealed record GroupRecord(int Group, string Launch);

    /// <summary>The launches this process runs: their records are not orphans, whoever looks.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LiveLaunches = new();

    /// <summary>
    /// Before a server recovers the sessions of the roots at <paramref name="rootPaths"/>, which it holds: ends
    /// the claudes a server that died without stopping them (a crash, a SIGKILL) left running, with every
    /// process they started. Each recorded group (<see cref="RecordPathFor"/>) that is not a launch of this
    /// process is taken whole, with any process that carries its launch's mark but left it (a session of its
    /// own), but only when a process in the group carries the mark: a group id is a pid, so the kernel can give
    /// it to another group once the session's is gone, and that group is left alone, only the marked processes
    /// elsewhere ended. A root whose records cannot be listed is skipped, logged, and the others are not. SIGTERM, then
    /// <see cref="OrphanGracePeriod"/>, then SIGKILL to what is left. Each record goes once it is dealt with.
    /// Linux only: Windows needs none (the Job Object dies with the server), and macOS has no group to record.
    /// </summary>
    public static async Task ReapOrphansAsync(IEnumerable<string> rootPaths, ILogger logger, TimeSpan? grace = null)
    {
        if (!OperatingSystem.IsLinux()) return;
        var records = rootPaths
            .SelectMany(root => RecordsIn(root, logger))
            .Select(path => (Path: path, Record: ReadRecord(path)))
            .Where(found => found.Record is not { } record || !LiveLaunches.ContainsKey(record.Launch))
            .ToList();
        if (records.Count == 0) return;

        await Task.WhenAll(records.Select(async found =>
        {
            if (found.Record is { } record)
            {
                try { await ReapAsync(found.Path, record, logger, grace ?? OrphanGracePeriod); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not reap the processes recorded at {Path}", found.Path); }
            }
            DeleteRecord(found.Path, logger);
        }));
    }

    /// <summary>The records in the root at <paramref name="rootPath"/>; none, logged, when its logs cannot be listed, so the other roots are still reaped and recovered.</summary>
    private static string[] RecordsIn(string rootPath, ILogger logger)
    {
        var logs = Path.Combine(rootPath, GodMode.ProjectFiles.ProjectFolder.ScriptLogsFolderName);
        try
        {
            return Directory.Exists(logs) ? Directory.GetFiles(logs, "*" + RecordSuffix) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not list the process group records in {Path}: what a server that died left running there is not reaped", logs);
            return [];
        }
    }

    private static async Task ReapAsync(string path, GroupRecord record, ILogger logger, TimeSpan grace)
    {
        var session = Path.GetFileName(path)[..^RecordSuffix.Length];
        var targets = Targets(record);
        if (targets.Members.Count == 0)
        {
            logger.LogInformation("Nothing of session {Session} is left running (group {Group}): none of the processes there carries its launch",
                session, record.Group);
            return;
        }
        // A server started from the orphaned session (a restart it ran) is one of them: it never ends itself
        if (targets.Members.Any(member => member.Pid == Environment.ProcessId))
        {
            logger.LogWarning("Session {Session} still has processes a server that died left running ({Pids}, group {Group}), and this server is one of them: left alone",
                session, Pids(targets), record.Group);
            return;
        }

        logger.LogWarning("Session {Session} still has processes a server that died left running: {Pids} ({Group}); ending them before it is recovered",
            session, Pids(targets), targets.WholeGroup ? $"group {record.Group}" : $"group {record.Group} is another's now, so only these");
        SignalTargets(record, targets, SigTerm);

        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline && Targets(record).Members.Count > 0) await Task.Delay(100);
        if (Targets(record) is { Members.Count: > 0 } left)
        {
            logger.LogWarning("Session {Session}: {Pids} did not end within {Grace}s of SIGTERM; killing them",
                session, Pids(left), grace.TotalSeconds);
            SignalTargets(record, left, SigKill);
        }
        else logger.LogInformation("Session {Session}: its orphaned processes ended on SIGTERM", session);
    }

    /// <summary>
    /// What a reap of <paramref name="record"/> ends: the whole group, with the marked processes that left it,
    /// when a process in the group carries the launch's mark; otherwise the group is another's (its id reused)
    /// and only the marked processes elsewhere are the session's. Decided afresh at each signal.
    /// </summary>
    private sealed record ReapTargets(IReadOnlyList<Member> Members, bool WholeGroup);

    private static ReapTargets Targets(GroupRecord record)
    {
        var members = Members(record);
        var wholeGroup = members.Any(member => member.Group == record.Group && member.Marked);
        return new ReapTargets(wholeGroup ? members : [.. members.Where(member => member.Marked)], wholeGroup);
    }

    private static void SignalTargets(GroupRecord record, ReapTargets targets, int signal)
    {
        if (targets.WholeGroup) Signal(-record.Group, signal);
        foreach (var member in targets.Members.Where(member => !targets.WholeGroup || member.Group != record.Group))
            Signal(member.Pid, signal);
    }

    private static string Pids(ReapTargets targets) => string.Join(", ", targets.Members.Select(member => member.Pid));

    private sealed record Member(int Pid, int Group, bool Marked);

    /// <summary>
    /// The live processes of <paramref name="record"/>'s group, and those elsewhere that carry its launch's
    /// mark, from <c>/proc</c>. A process another user owns (its environment unreadable) carries none.
    /// </summary>
    private static List<Member> Members(GroupRecord record)
    {
        var mark = Encoding.UTF8.GetBytes($"\0{LaunchVariable}={record.Launch}\0");
        var members = new List<Member>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || ProcessGroupOf(pid) is not { } group) continue;
            var marked = Carries(pid, mark);
            if (group == record.Group || marked) members.Add(new Member(pid, group, marked));
        }
        return members;
    }

    /// <summary>A Linux process's group (<c>/proc/{pid}/stat</c>); null once it is gone or a zombie, which holds nothing.</summary>
    private static int? ProcessGroupOf(int pid)
    {
        try
        {
            // "pid (comm) state ppid pgrp …", and comm can hold spaces and parentheses
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields[0] == "Z" ? null : int.Parse(fields[2]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool Carries(int pid, byte[] mark)
    {
        try
        {
            // Each entry ends with a NUL, and the mark is one entry: NUL-led, so the first is one too
            byte[] environ = [0, .. File.ReadAllBytes($"/proc/{pid}/environ")];
            return environ.AsSpan().IndexOf(mark) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static GroupRecord? ReadRecord(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<GroupRecord>(File.ReadAllText(path)) is { Group: > 1, Launch.Length: > 0 } record ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void DeleteRecord(string path, ILogger logger)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the process group record {Path}", path);
        }
    }

    /// <summary>No <c>setsid</c>: claude stays in the server's group, and a stop reaches it and the children it still has.</summary>
    private sealed class ProcessOnlyTree(ILogger logger) : SessionProcessTree(logger)
    {
        private static int _warned;
        private Process? _process;

        public override void Prepare(ProcessStartInfo startInfo)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                Logger.LogWarning("setsid is not on PATH: claude shares the server's process group, and a Ctrl+C in its terminal reaches claude too");
        }

        public override void Attach(Process process) => _process = process;

        public override Task InterruptAsync()
        {
            if (_process is { } process) Signal(process.Id, SigInt);
            return Task.CompletedTask;
        }

        protected override void KillTree()
        {
            if (_process is { HasExited: false } process) process.Kill(entireProcessTree: true);
        }

        protected override void Release() { }
    }

    private const int SigInt = 2;
    private const int SigKill = 9;
    private const int SigTerm = 15;

    /// <summary>kill(2); a group that is gone already (ESRCH) is not an error.</summary>
    private static void Signal(int target, int signal)
    {
        if (target is 0 or -1) return;
        if (kill(target, signal) != 0 && Marshal.GetLastPInvokeError() is var errno && errno != Esrch)
            throw new Win32Exception(errno);
    }

    private const int Esrch = 3;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static string? FindOnPath(string fileName, string? path) =>
        (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(candidate => File.Exists(candidate)
                && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0));
}
