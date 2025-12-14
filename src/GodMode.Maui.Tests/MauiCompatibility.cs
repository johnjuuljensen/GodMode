// Compatibility shims for MAUI types that don't exist in test environment
// These allow ViewModels to compile without MAUI dependencies

namespace Microsoft.Maui.Controls;

/// <summary>
/// Stub for MAUI QueryPropertyAttribute - allows ViewModels to compile in test project
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class QueryPropertyAttribute : Attribute
{
    public QueryPropertyAttribute(string propertyName, string queryId) { }
}

/// <summary>
/// Stub for MAUI Shell navigation - tests should not actually navigate
/// </summary>
public static class Shell
{
    public static ShellStub? Current { get; set; }
}

public class ShellStub
{
    public Task GoToAsync(string route) => Task.CompletedTask;
    public Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel) => Task.FromResult(false);
}

/// <summary>
/// Stub for MAUI Window
/// </summary>
public class Window
{
    public Page? Page { get; set; }
}

/// <summary>
/// Stub for MAUI Application
/// </summary>
public class Application
{
    public static Application? Current { get; set; }
    public Page? MainPage { get; set; }
    public IList<Window> Windows { get; } = new List<Window> { new Window() };
}

public class Page
{
    public Task DisplayAlert(string title, string message, string cancel) => Task.CompletedTask;
    public Task DisplayAlertAsync(string title, string message, string cancel) => Task.CompletedTask;
}

/// <summary>
/// Stub for MAUI MainThread
/// </summary>
public static class MainThread
{
    public static void BeginInvokeOnMainThread(Action action) => action();
}
