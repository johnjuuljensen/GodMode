namespace GodMode.ClientBase.Layout;

/// <summary>Where a saved window opens: its bounds on a visible screen, and its desktop, or null for the current one.</summary>
public sealed record WindowPlace(WindowBounds Bounds, bool Maximized, Guid? Desktop);

/// <summary>
/// What a restart does with each saved window (#341). A profile that no longer exists isn't reopened; a desktop that
/// no longer exists means the current one; bounds no screen shows (a monitor removed) are brought onto the first.
/// </summary>
public static class WindowPlacer
{
    /// <summary>How much of the title bar's strip a screen must show for the window to count as visible on it.</summary>
    public const int TitleStripHeight = 32;
    public const int MinimumVisibleWidth = 100;

    /// <summary>
    /// Whether the saved profile's window is reopened: always, unless every server answered and none has the profile.
    /// A server that is slow or offline might have it, so an incomplete census keeps it.
    /// </summary>
    public static bool Reopens(string profile, ProfileCensus census) =>
        !census.Complete || census.Names.Contains(profile, StringComparer.OrdinalIgnoreCase);

    /// <param name="desktopExists">Whether the virtual desktop is there now.</param>
    /// <param name="screens">The screens' work areas, the primary first. None means they aren't known: bounds stay.</param>
    public static WindowPlace Place(SavedWindow saved, Func<Guid, bool> desktopExists, IReadOnlyList<WindowBounds> screens) =>
        new(OnScreen(saved.Bounds, screens), saved.Maximized, saved.Desktop is { } desktop && desktopExists(desktop) ? desktop : null);

    /// <summary>The bounds as they are when a screen shows enough of the title bar to drag it; else the same size,
    /// shrunk to fit, centred on the first screen.</summary>
    public static WindowBounds OnScreen(WindowBounds bounds, IReadOnlyList<WindowBounds> screens)
    {
        if (screens.Count == 0 || screens.Any(s => ShowsTitleBar(s, bounds))) return bounds;
        var screen = screens[0];
        var width = Math.Clamp(bounds.Width, 1, screen.Width);
        var height = Math.Clamp(bounds.Height, 1, screen.Height);
        return new(screen.X + (screen.Width - width) / 2, screen.Y + (screen.Height - height) / 2, width, height);
    }

    private static bool ShowsTitleBar(WindowBounds screen, WindowBounds window)
    {
        var width = Math.Min(screen.X + screen.Width, window.X + window.Width) - Math.Max(screen.X, window.X);
        var height = Math.Min(screen.Y + screen.Height, window.Y + TitleStripHeight) - Math.Max(screen.Y, window.Y);
        return width >= Math.Min(MinimumVisibleWidth, window.Width) && height >= TitleStripHeight / 2;
    }
}
