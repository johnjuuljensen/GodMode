using Microsoft.Extensions.Logging;
using GodMode.Maui.Services;
using GodMode.Maui.ViewModels;
using GodMode.Maui.Views;

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
#endif

		// Register Services
		builder.Services.AddSingleton<ProfileService>();
		builder.Services.AddSingleton<HostConnectionService>();
		builder.Services.AddSingleton<ProjectService>();
		builder.Services.AddSingleton<NotificationService>();

		// Register ViewModels
		builder.Services.AddTransient<MainViewModel>();
		builder.Services.AddTransient<HostViewModel>();
		builder.Services.AddTransient<ProjectViewModel>();
		builder.Services.AddTransient<AddProfileViewModel>();
		builder.Services.AddTransient<CreateProjectViewModel>();

		// Register Views
		builder.Services.AddTransient<MainPage>();
		builder.Services.AddTransient<HostPage>();
		builder.Services.AddTransient<ProjectPage>();
		builder.Services.AddTransient<AddProfilePage>();
		builder.Services.AddTransient<CreateProjectPage>();

		return builder.Build();
	}
}
