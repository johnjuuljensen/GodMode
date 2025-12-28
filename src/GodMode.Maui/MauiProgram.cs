using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
		builder.Services.AddLogging(configure => configure.AddDebug());
#endif

		// Register Services
		builder.Services.AddSingleton<IProfileService>(_ => new ProfileService(FileSystem.AppDataDirectory));
		builder.Services.AddSingleton<IHostConnectionService, HostConnectionService>();
		builder.Services.AddSingleton<IProjectService, ProjectService>();
		builder.Services.AddSingleton<INotificationService, NotificationService>();

		// Register ViewModels
		builder.Services.AddTransient<MainViewModel>();
		builder.Services.AddTransient<HostViewModel>();
		builder.Services.AddTransient<ProjectViewModel>();
		builder.Services.AddTransient<AddProfileViewModel>();
		builder.Services.AddTransient<AddServerViewModel>();
		builder.Services.AddTransient<EditServerViewModel>();
		builder.Services.AddTransient<CreateProjectViewModel>();

		// Register Views
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<HostPage>();
		builder.Services.AddTransient<ProjectPage>();
		builder.Services.AddTransient<AddProfilePage>();
		builder.Services.AddTransient<AddServerPage>();
		builder.Services.AddTransient<EditServerPage>();
		builder.Services.AddTransient<CreateProjectPage>();

		return builder.Build();
	}
}
