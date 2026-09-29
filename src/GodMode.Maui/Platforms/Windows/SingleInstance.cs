using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.Win32.SafeHandles;

namespace GodMode.Maui;

/// <summary>
/// One app per user in release builds (issue #339). The first start holds a named mutex and listens on a named pipe of
/// the same name; a later start finds the mutex held, sends its arguments down the pipe and exits, and the running app
/// comes to the front. Debug builds (no SINGLE_INSTANCE, see the csproj) take neither, so they run alongside the
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
    /// A later start handed its arguments (without the exe) to this app, on the main thread, after its window came to
    /// the front.
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
            _ = Task.Run(() => ListenAsync(logger));
            logger.LogInformation("Single instance: this process is the app, listening for later starts");
            return true;
        }

        mutex.Dispose();
        HandOff(args, logger);
        return false;
#else
        logger.LogInformation("Single instance: not in this build (debug), so it runs alongside any other");
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
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            logger.LogWarning(ex, "Single instance: the app holds the mutex but took no hand-off; exiting");
        }
    }

    private static async Task ListenAsync(ILogger logger)
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync();
                var args = await JsonSerializer.DeserializeAsync<string[]>(pipe) ?? [];
                logger.LogInformation("Single instance: a later start handed off [{Args}]; bringing the window to the front",
                    string.Join(' ', args));
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    BringToFront(logger);
                    HandedOff?.Invoke(args);
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Single instance: a hand-off failed");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    /// <summary>
    /// Restores the main window if it is minimised and makes it the foreground window, which switches to its virtual
    /// desktop.
    /// </summary>
    private static void BringToFront(ILogger logger)
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window)
            return;
        if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        window.Activate();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!SetForegroundWindow(handle) || GetForegroundWindow() != handle)
            logger.LogInformation("Single instance: Windows kept the foreground from the window");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
