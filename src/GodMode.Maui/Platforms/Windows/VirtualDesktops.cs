using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GodMode.Maui;

/// <summary>
/// The app's windows' virtual desktops (#341), through <c>IVirtualDesktopManager</c> (public COM, shobjidl_core.h):
/// which desktop a window is on, and moving the app's own window to one. Windows only lets a process move its own
/// windows, which is all the app does.
/// </summary>
internal static class VirtualDesktops
{
    private static readonly Guid ManagerClass = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    /// <summary>Explorer's list of the desktops there are, as 16-byte GUIDs, one after the other.</summary>
    private const string DesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    private static readonly Lazy<IVirtualDesktopManager?> Manager = new(() =>
    {
        try
        {
            return Type.GetTypeFromCLSID(ManagerClass) is { } type ? Activator.CreateInstance(type) as IVirtualDesktopManager : null;
        }
        catch (COMException)
        {
            return null;
        }
    });

    /// <summary>The desktop the window is on, or null when Windows doesn't say (no desktops, or not shown yet).</summary>
    public static Guid? Of(nint window) =>
        Manager.Value is { } manager && manager.GetWindowDesktopId(window, out var desktop) >= 0 && desktop != Guid.Empty
            ? desktop
            : null;

    /// <summary>
    /// Whether the desktop is there now. Explorer's list is how it's known before a window is moved; when that can't be
    /// read, it's taken as there, and a failed move leaves the window on the current desktop.
    /// </summary>
    public static bool Exists(Guid desktop)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopsKey);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || ids.Length % 16 != 0) return true;
            return Enumerable.Range(0, ids.Length / 16).Any(i => new Guid(ids.AsSpan(i * 16, 16)) == desktop);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The desktop the user is on, as Explorer keeps it; null when that can't be read.</summary>
    public static Guid? Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopsKey);
            return key?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } id ? new Guid(id) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Moves the app's window to the desktop. False, logged, when Windows refuses.</summary>
    public static bool MoveTo(nint window, Guid desktop, ILogger logger)
    {
        if (Manager.Value is not { } manager)
        {
            logger.LogInformation("Windows: no virtual desktop manager; the window stays on the current desktop");
            return false;
        }
        var result = manager.MoveWindowToDesktop(window, desktop);
        if (result >= 0) return true;
        logger.LogInformation("Windows: could not move the window to desktop {Desktop} (0x{Result:X8}); it stays on the current one",
            desktop, result);
        return false;
    }

    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(nint topLevelWindow, [MarshalAs(UnmanagedType.LPStruct)] Guid desktopId);
    }
}
