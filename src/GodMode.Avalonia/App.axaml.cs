using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GodMode.Avalonia.Services;
using GodMode.Avalonia.Tools;
using GodMode.Avalonia.Voice;
using GodMode.Voice;
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

		// Fire-and-forget: initialize inference router at startup
		_ = Task.Run(async () =>
		{
			var assistant = Services.GetRequiredService<AssistantService>();
			await assistant.InitializeAsync();
		});

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new MainWindow
			{
				DataContext = Services.GetRequiredService<MainWindowViewModel>()
			};
		}

		base.OnFrameworkInitializationCompleted();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		// ClientBase services - shared config at ~/.godmode
		services.AddGodModeClientServices();

		// Preload platform assemblies so they're discoverable via reflection.
		// Without this, GodMode.Voice.Windows / GodMode.AI.LocalInference.Windows
		// may not be in AppDomain.GetAssemblies() yet (lazy loading).
		foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, "GodMode.*.dll"))
		{
			try { Assembly.LoadFrom(dll); }
			catch { /* non-managed or duplicate — skip */ }
		}

		// Auto-discover and register platform-specific services
		// (AI inference, speech implementations)
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
		}

		// Cross-platform voice services (TryAdd — platform overrides already registered above)
		services.AddGodModeVoiceServices();

		// Voice context — stateful session tracking
		services.AddSingleton<VoiceContext>();

		// Voice tool registry — wired to real app services via VoiceContext
		services.AddSingleton<ToolRegistry>(sp =>
		{
			var ctx = sp.GetRequiredService<VoiceContext>();
			var profiles = sp.GetRequiredService<IProfileService>();
			var hosts = sp.GetRequiredService<IHostConnectionService>();
			var projects = sp.GetRequiredService<IProjectService>();

			var registry = new ToolRegistry();
			registry.Register(new RespondTool());
			registry.Register(new SetProfileTool(ctx, profiles));
			registry.Register(new SetServerTool(ctx, hosts));
			registry.Register(new ListProfilesTool(ctx, profiles));
			registry.Register(new ListServersTool(ctx, hosts));
			registry.Register(new ListProjectsTool(ctx));
			registry.Register(new ProjectStatusTool(ctx, projects));
			registry.Register(new CreateProjectTool(ctx, projects));
			registry.Register(new SendInputTool(ctx, projects));
			registry.Register(new StopProjectTool(ctx, projects));
			registry.Register(new ResumeProjectTool(ctx, projects));
			registry.Register(new FocusProjectTool(ctx));
			registry.Register(new UnfocusProjectTool(ctx));
			return registry;
		});

		// Avalonia-specific services
		services.AddSingleton<INavigationService, NavigationService>();
		services.AddSingleton<IDialogService, DialogService>();
		services.AddSingleton<IThemeService, ThemeService>();
		// services.AddSingleton<IEmbeddedServerService, EmbeddedServerService>(); // Disabled — server managed externally

		// ViewModels — singletons for state preservation
		services.AddSingleton<VoiceAssistantViewModel>();
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
