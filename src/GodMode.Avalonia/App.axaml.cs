using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GodMode.Avalonia.Tools;
using GodMode.Avalonia.Voice;
using GodMode.Voice;
using GodMode.Voice.Services;
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

		// Fire-and-forget: load AI model at startup if configured
		_ = Task.Run(async () =>
		{
			var config = InferenceConfig.Load();
			var provider = config.ExecutionProvider?.ToLowerInvariant() ?? "auto";
			var modelPath = ResolveModelPath(config, provider);

			if (modelPath is not null)
			{
				var assistant = Services.GetRequiredService<AssistantService>();
				await assistant.InitializeModelAsync(modelPath);
			}
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

	private static string? ResolveModelPath(InferenceConfig config, string provider)
	{
		// NPU model: requires tokenizer.json (standard ONNX Runtime, not OGA)
		if (provider is "npu" or "auto" && !string.IsNullOrEmpty(config.NpuModelPath) &&
			Directory.Exists(config.NpuModelPath) &&
			File.Exists(Path.Combine(config.NpuModelPath, "tokenizer.json")))
		{
			return config.NpuModelPath;
		}

		// DirectML/OGA model: requires genai_config.json
		if (!string.IsNullOrEmpty(config.Phi4ModelPath) &&
			Directory.Exists(config.Phi4ModelPath) &&
			File.Exists(Path.Combine(config.Phi4ModelPath, "genai_config.json")))
		{
			return config.Phi4ModelPath;
		}

		return null;
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
		services.AddSingleton<VoiceAssistantViewModel>();
		services.AddTransient<MainWindowViewModel>();
		services.AddTransient<MainViewModel>();
		services.AddTransient<HostViewModel>();
		services.AddTransient<ProjectViewModel>();
		services.AddTransient<AddProfileViewModel>();
		services.AddTransient<AddServerViewModel>();
		services.AddTransient<EditServerViewModel>();
		services.AddTransient<CreateProjectViewModel>();
	}
}
