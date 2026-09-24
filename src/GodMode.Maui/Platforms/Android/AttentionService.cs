using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using GodMode.ClientBase.Attention;
using GodMode.ClientBase.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>
/// Keeps the phone told what needs the user while the app is in the background: a foreground service holding an
/// <see cref="AttentionWatcher"/>, one hub connection per registered server, straight to the server with its key
/// from the registry. It runs while any server is registered and notifications are allowed.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ServiceType)]
public sealed class AttentionService : Service
{
    /// <summary>The issue's choice. Android 15 limits it to 6 hours a day, after which <see cref="OnTimeout(int, ForegroundService)"/> stops the service.</summary>
#pragma warning disable CA1416 // Android below 10 has no service types and ignores it
    private const ForegroundService ServiceType = ForegroundService.TypeDataSync;
#pragma warning restore CA1416

    private const string ServiceChannel = "attention-service";
    private const int ServiceNotificationId = 100;
    private const string ActionRefresh = "com.godmode.maui.attention.REFRESH";

    private AttentionWatcher? _watcher;
    private AttentionNotifier? _notifier;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Starts the service, or has it pick up a change to the server list. Call it from the foreground app:
    /// Android does not let a backgrounded app start a foreground service.
    /// </summary>
    public static void Refresh()
    {
        var context = Platform.AppContext;
        // Without POST_NOTIFICATIONS there is nothing to show, so nothing to watch for
        if (!NotificationManagerCompat.From(context)!.AreNotificationsEnabled()) return;
        var intent = new Intent(context, typeof(AttentionService)).SetAction(ActionRefresh);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
        }
        catch (Exception ex)
        {
            // ForegroundServiceStartNotAllowedException when the app is not in the foreground after all
            MauiProgram.LoggerFactory.CreateLogger<AttentionService>().LogWarning("Could not start the attention service: {Error}", ex.Message);
        }
    }

    /// <summary>Asks for POST_NOTIFICATIONS (Android 13+) and, when it is granted, starts the service.</summary>
    public static async Task StartAsync()
    {
        if (await Permissions.RequestAsync<Permissions.PostNotifications>() == PermissionStatus.Granted)
            Refresh();
        else
            MauiProgram.LoggerFactory.CreateLogger<AttentionService>().LogInformation("Notifications are not allowed; not watching for attention");
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _logger = MauiProgram.LoggerFactory.CreateLogger<AttentionService>();
        AttentionNotifier.CreateChannel(this);
        CreateServiceChannel();
        _notifier = new AttentionNotifier(this, MauiProgram.LoggerFactory.CreateLogger<AttentionNotifier>());
        var services = MauiProgram.Services;
        _watcher = new AttentionWatcher(services.GetRequiredService<IServerDirectory>(), _notifier, MauiProgram.LoggerFactory);
        Connectivity.Current.ConnectivityChanged += OnNetworkChanged;
        _logger.LogInformation("Attention service created");
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Within 5 s of StartForegroundService, whatever else happens
        var builder = new NotificationCompat.Builder(this, ServiceChannel);
        builder.SetSmallIcon(Resource.Drawable.ic_attention);
        builder.SetContentTitle("Watching for what needs you");
        builder.SetOngoing(true);
        builder.SetSilent(true);
        builder.SetContentIntent(PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable));
        var notification = builder.Build()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(ServiceNotificationId, notification, ServiceType);
        else
            StartForeground(ServiceNotificationId, notification);

        _ = RefreshAsync();
        // Restarted after the system kills it for memory; the watcher then starts afresh from the registry
        return StartCommandResult.Sticky;
    }

    private async Task RefreshAsync()
    {
        try
        {
            if ((await MauiProgram.Services.GetRequiredService<IServerRegistryService>().GetServersAsync()).Count == 0)
            {
                _logger.LogInformation("No servers registered; stopping the attention service");
                StopSelf();
                return;
            }
            await _watcher!.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Attention refresh failed");
        }
    }

    private void OnNetworkChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        _logger.LogInformation("Network changed ({Access}); reconnecting attention", e.NetworkAccess);
        _watcher?.Reconnect();
    }

    /// <summary>Android 15's daily limit for a dataSync service is spent: stop now, or the app is killed. Opening the app starts it again.</summary>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        _logger.LogWarning("Attention service reached Android's time limit; stopping");
        StopSelf();
    }

    public override void OnDestroy()
    {
        Connectivity.Current.ConnectivityChanged -= OnNetworkChanged;
        var watcher = _watcher;
        _watcher = null;
        // Without the connections nothing clears a notification any more, so none is left behind
        _ = Task.Run(async () =>
        {
            if (watcher != null) await watcher.DisposeAsync();
            _notifier?.CancelAll();
        });
        _logger.LogInformation("Attention service destroyed");
        base.OnDestroy();
    }

    private void CreateServiceChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(ServiceChannel, "Watching servers", NotificationImportance.Min)
        {
            Description = "Shown while GodMode keeps a connection to your servers for notifications",
        };
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
    }
}
