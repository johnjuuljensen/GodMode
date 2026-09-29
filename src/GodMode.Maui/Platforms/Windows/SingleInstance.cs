using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Win32.SafeHandles;

namespace GodMode.Maui;

/// <summary>
/// One app per user in release builds (issue #339). The first start holds a named mutex and listens on a named pipe of
/// the same name; a later start finds the mutex held, sends its arguments down the pipe and exits, and the running app
/// comes to the front: the window of the profile it names (<c>--profile Work</c>, <see cref="AppWindows.HandOff"/>),
/// else the main window. Debug builds (no SINGLE_INSTANCE, see the csproj) take neither, so they run alongside the
/// installed app. The mutex is in the Global namespace and named for the user, so it is app-wide across the user's
/// sessions and never blocks another user's app.
/// </summary>
internal static class SingleInstance
{
    private static readonly string Name = $"GodMode.Maui.{WindowsIdentity.GetCurrent().User!.Value}";

    /// <summary>How long a start waits for the running app to take its hand-off.</summary>
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(5);

#if SINGLE_INSTANCE
    /// <summary>Held for the life of the process; Windows releases it when the process ends, however it ends.</summary>
    private static Mutex? _mutex;
#endif

    /// <summary>
    /// A later start handed its arguments (without the exe) to this app, on the main thread, after the window they name
    /// came to the front.
    /// </summary>
    public static event Action<IReadOnlyList<string>>? HandedOff;

    /// <summary>
    /// Makes this process the app, or hands its start to the app already running. False when it handed off, and is to
    /// exit before it starts anything.
    /// </summary>
    public static bool Claim(IReadOnlyList<string> args, ILogger logger)
    {
#if SINGLE_INSTANCE
        var mutex = new Mutex(initiallyOwned: false, $@"Global\{Name}");
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the last app ended without releasing it: this one has it now
        }

        if (owned)
        {
            _mutex = mutex;
            // Claim runs on the UI thread. Its queue takes a hand-off before the first window exists, which
            // MainThread's (found through the active window) doesn't
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _ = Task.Run(() => ListenAsync(dispatcher, logger));
            logger.LogInformation("Single instance: this process is the app, listening for later starts");
            return true;
        }

        mutex.Dispose();
        HandOff(args, logger);
        return false;
#else
        logger.LogInformation("Single instance: this build takes no mutex (a debug build, or SingleInstance=false), so it runs alongside any other");
        return true;
#endif
    }

    private static void HandOff(IReadOnlyList<string> args, ILogger logger)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectWait);
            // Windows lets the running app take the foreground only if this start, the one the user just made, allows it
            var pid = GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) ? serverPid : 0;
            var foreground = pid != 0 && AllowSetForegroundWindow(pid);
            JsonSerializer.Serialize(pipe, args);
            pipe.WaitForPipeDrain();
            logger.LogInformation(
                "Single instance: the app is running (process {Pid}, may take the foreground: {Foreground}); handed off this start [{Args}], exiting",
                pid, foreground, string.Join(' ', args));
        }
        // UnauthorizedAccessException: a pipe of that name that isn't this user's
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Single instance: the app holds the mutex but took no hand-off; exiting");
        }
    }

    private static async Task ListenAsync(DispatcherQueue dispatcher, ILogger logger)
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                var args = await JsonSerializer.DeserializeAsync<string[]>(pipe) ?? [];
                logger.LogInformation("Single instance: a later start handed off [{Args}]; bringing its window to the front",
                    string.Join(' ', args));
                if (!dispatcher.TryEnqueue(() =>
                    {
                        AppWindows.HandOff(args, logger);
                        HandedOff?.Invoke(args);
                    }))
                    logger.LogWarning("Single instance: the app is shutting down, and drops the hand-off [{Args}]",
                        string.Join(' ', args));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Single instance: a hand-off failed");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
