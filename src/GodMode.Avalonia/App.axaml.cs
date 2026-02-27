using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GodMode.Avalonia.Tools;
using GodMode.Avalonia.Voice;
using GodMode.Voice;
using GodMode.Voice.Tools;
using GodMode.Voice.Windows;
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

		base.OnFrameworkInitializationCompleted();
	}

	private static void ConfigureServices(IServiceCollection services)
	{
		// ClientBase services - shared config at ~/.godmode
		services.AddGodModeClientServices();

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

		// Avalonia-specific services
		services.AddSingleton<INavigationService, NavigationService>();
		services.AddSingleton<IDialogService, DialogService>();

		// ViewModels
		services.AddTransient<MainWindowViewModel>();
		services.AddTransient<MainViewModel>();
		services.AddTransient<HostViewModel>();
		services.AddTransient<ProjectViewModel>();
		services.AddTransient<AddProfileViewModel>();
		services.AddTransient<AddServerViewModel>();
		services.AddTransient<EditServerViewModel>();
		services.AddTransient<CreateProjectViewModel>();
		services.AddTransient<VoiceAssistantViewModel>();
	}
}
