using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GodMode.Avalonia.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Avalonia;

public partial class App : Application
{
	public static IServiceProvider Services { get; private set; } = null!;

	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public override void OnFrameworkInitializationCompleted()
	{
		var services = new ServiceCollection();
		ConfigureServices(services);
		Services = services.BuildServiceProvider();

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			// Auto-start embedded server
			var embeddedServer = Services.GetRequiredService<IEmbeddedServerService>();
			_ = embeddedServer.StartAsync();

			desktop.MainWindow = new MainWindow
			{
				DataContext = Services.GetRequiredService<MainWindowViewModel>()
			};

			desktop.ShutdownRequested += (_, _) =>
			{
				embeddedServer.Stop();
			};
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		// ClientBase services - shared config at ~/.godmode
		services.AddGodModeClientServices();

		// Avalonia-specific services
		services.AddSingleton<INavigationService, NavigationService>();
		services.AddSingleton<IDialogService, DialogService>();
		services.AddSingleton<IThemeService, ThemeService>();
		services.AddSingleton<IEmbeddedServerService, EmbeddedServerService>();

		// ViewModels
		services.AddSingleton<MainWindowViewModel>();
		services.AddSingleton<MainViewModel>();
		services.AddTransient<HostViewModel>();
		services.AddTransient<ProjectViewModel>();
		services.AddTransient<AddProfileViewModel>();
		services.AddTransient<AddServerViewModel>();
		services.AddTransient<EditServerViewModel>();
		services.AddTransient<CreateProjectViewModel>();
		services.AddTransient<TileGridViewModel>();
	}
}
