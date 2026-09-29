using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;

namespace GodMode.Maui;

/// <summary>Brings one of the app's windows to the front.</summary>
internal static class WindowFront
{
    /// <summary>
    /// Restores the window if it is minimised and makes it the foreground window, which switches to its virtual
    /// desktop.
    /// </summary>
    public static void Bring(Window window, ILogger logger)
    {
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platform)
        {
            logger.LogInformation("Windows: the window is not shown yet, so not brought to the front");
            return;
        }
        if (platform.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        platform.Activate();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(platform);
        if (!SetForegroundWindow(handle) || GetForegroundWindow() != handle)
            logger.LogInformation("Windows: Windows kept the foreground from the window");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
