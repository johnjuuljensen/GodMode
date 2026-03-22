using System.Reflection;
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
			desktop.MainWindow = new MainWindow
			{
				DataContext = Services.GetRequiredService<MainWindowViewModel>()
			};
		}
		else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
		{
			var vm = Services.GetRequiredService<MainWindowViewModel>();
			vm.IsCompact = true;
			singleView.MainView = new ShellView { DataContext = vm };
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		// ClientBase services - shared config at ~/.godmode
		services.AddGodModeClientServices();

		// Preload platform assemblies so they're discoverable via reflection.
		// Without this, GodMode.AI.LocalInference.Windows
		// may not be in AppDomain.GetAssemblies() yet (lazy loading).
		foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, "GodMode.*.dll"))
		{
			try { Assembly.LoadFrom(dll); }
			catch { /* non-managed or duplicate — skip */ }
		}

		// Auto-discover and register platform-specific services (AI inference)
		foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
		{
			try
			{
				foreach (var type in asm.GetExportedTypes()
					.Where(t => typeof(IPlatformServiceRegistrar).IsAssignableFrom(t) && !t.IsAbstract))
				{
					((IPlatformServiceRegistrar)Activator.CreateInstance(type)!).RegisterServices(services);
				}
			}
			catch (NotSupportedException)
			{
				// Some assemblies don't support GetExportedTypes
			}
			catch (ReflectionTypeLoadException)
			{
				// Assembly has types with missing dependencies (e.g. MAUI) — skip
			}
		}

		// AI services (Anthropic, InferenceRouter, etc.)
		services.AddGodModeAIServices();

		// Avalonia-specific services
		services.AddSingleton<INavigationService, NavigationService>();
		services.AddSingleton<IDialogService, DialogService>();
		services.AddSingleton<IThemeService, ThemeService>();

		// ViewModels — singletons for state preservation
		services.AddSingleton<MainWindowViewModel>();
		services.AddSingleton<MainViewModel>();
		services.AddTransient<HostViewModel>();
		services.AddTransient<ProjectViewModel>();
		services.AddTransient<AddServerViewModel>();
		services.AddTransient<EditServerViewModel>();
		services.AddTransient<CreateProjectViewModel>();
		services.AddTransient<DeleteConfirmViewModel>();
		services.AddTransient<TileGridViewModel>();
	}
}
