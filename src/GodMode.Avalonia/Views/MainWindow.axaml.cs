using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace GodMode.Avalonia.Views;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();

		// Subscribe to notification service for dock bounce on macOS
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			var notificationService = App.Services.GetService(
				typeof(INotificationService)) as INotificationService;
			if (notificationService != null)
			{
				notificationService.NotificationRequested += (_, args) =>
				{
					// Request macOS dock attention when project needs input
					if (!IsActive)
						RequestDockBounce();
				};
			}
		}
	}

	private void RequestDockBounce()
	{
		try
		{
			// Use NSApplication.requestUserAttention via reflection to avoid hard macOS dependency
			var nsAppType = Type.GetType("AppKit.NSApplication, Xamarin.Mac") ??
			                Type.GetType("AppKit.NSApplication, Microsoft.macOS");

			if (nsAppType != null)
			{
				var sharedApp = nsAppType.GetProperty("SharedApplication")?.GetValue(null);
				// RequestUserAttention(NSRequestUserAttentionType.InformationalRequest = 10)
				nsAppType.GetMethod("RequestUserAttention")?.Invoke(sharedApp, [10]);
			}
		}
		catch
		{
			// Silently ignore if AppKit is not available
		}
	}
}
