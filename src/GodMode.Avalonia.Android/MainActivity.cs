using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;
using GodMode.Avalonia;

namespace GodMode.Avalonia.Android;

[Activity(Label = "@string/app_name", Theme = "@style/GodMode.NoActionBar", MainLauncher = true,
	ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
	protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
		=> base.CustomizeAppBuilder(builder)
			.WithInterFont()
			.LogToTrace();
}
