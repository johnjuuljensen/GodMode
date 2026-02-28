using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GodMode.Avalonia.Services;
#if VOICE_ENABLED
using GodMode.Avalonia.Tools;
using GodMode.Avalonia.Voice;
using GodMode.Voice;
using GodMode.Voice.Services;
using GodMode.Voice.Tools;
using GodMode.Voice.Windows;
#endif
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

#if VOICE_ENABLED
		// Fire-and-forget: load AI model at startup if configured
		_ = Task.Run(async () =>
		{
			var config = InferenceConfig.Load();
			if (!string.IsNullOrEmpty(config.Phi4ModelPath) &&
				Directory.Exists(config.Phi4ModelPath) &&
				File.Exists(Path.Combine(config.Phi4ModelPath, "genai_config.json")))
			{
				var assistant = Services.GetRequiredService<AssistantService>();
				await assistant.InitializeModelAsync(config.Phi4ModelPath);
			}
		});
#endif

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

#if VOICE_ENABLED
		// Voice services — Windows speech first (before TryAdd fallbacks)
		services.AddGodModeWindowsSpeech();
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
#endif

		// Avalonia-specific services
		services.AddSingleton<INavigationService, NavigationService>();
		services.AddSingleton<IDialogService, DialogService>();
		services.AddSingleton<IThemeService, ThemeService>();
		services.AddSingleton<IEmbeddedServerService, EmbeddedServerService>();

		// ViewModels
#if VOICE_ENABLED
		services.AddSingleton<VoiceAssistantViewModel>();
#endif
		services.AddSingleton<MainWindowViewModel>();
		services.AddSingleton<MainViewModel>();
		services.AddTransient<HostViewModel>();
		services.AddTransient<ProjectViewModel>();
		services.AddTransient<AddProfileViewModel>();
		services.AddTransient<AddServerViewModel>();
		services.AddTransient<EditServerViewModel>();
		services.AddTransient<CreateProjectViewModel>();
		services.AddTransient<CredentialListViewModel>();
		services.AddTransient<TileGridViewModel>();
	}
}
