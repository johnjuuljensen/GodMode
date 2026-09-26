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
/// from the registry. It runs only while it has something to do: notifications are on and it watches a server.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ServiceType)]
public sealed class AttentionService : Service
{
    /// <summary>
    /// What the service does: it receives messages from the user's own servers. Android sets this type no time limit,
    /// where dataSync gets 6 hours a day from Android 15 on.
    /// </summary>
#pragma warning disable CA1416 // Android 14 named the type; 10 to 13 check only that it is the manifest's, and below 10 there are none
    private const ForegroundService ServiceType = ForegroundService.TypeRemoteMessaging;
#pragma warning restore CA1416

    private const string ServiceChannel = "attention-service";
    private const int ServiceNotificationId = 100;
    private const string ActionRefresh = "com.godmode.maui.attention.REFRESH";

    private AttentionWatcher? _watcher;
    private AttentionNotifier? _notifier;
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Starts the service, or has it pick up a change to the server list; with notifications off, stops it. Call it
    /// from the foreground app: Android does not let a backgrounded app start a foreground service.
    /// </summary>
    public static void Refresh()
    {
        var context = Platform.AppContext;
        var intent = new Intent(context, typeof(AttentionService)).SetAction(ActionRefresh);
        // Without notifications there is nothing to show, so nothing to watch for. Android 7-12 turns them off
        // without stopping the app, so a running service is stopped here
        if (!NotificationsEnabled(context))
        {
            context.StopService(intent);
            return;
        }
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

    /// <summary>Asks for POST_NOTIFICATIONS (Android 13+), then starts the service, or stops it when notifications are off.</summary>
    public static async Task StartAsync()
    {
        if (await Permissions.RequestAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            MauiProgram.LoggerFactory.CreateLogger<AttentionService>().LogInformation("Notifications are not allowed; not watching for attention");
        Refresh();
    }

    private static bool NotificationsEnabled(Context context) => NotificationManagerCompat.From(context)!.AreNotificationsEnabled();

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

        _ = RefreshAsync(startId);
        // Restarted after the system kills it for memory; the watcher then starts afresh from the registry
        return StartCommandResult.Sticky;
    }

    private async Task RefreshAsync(int startId)
    {
        try
        {
            // Checked here too: a sticky restart, or notifications turned off while it ran
            if (!NotificationsEnabled(this))
                Stop(startId, "Notifications are off");
            // No registration, or none that can be listed (a token secure storage cannot read, say)
            else if (await _watcher!.RefreshAsync() == 0)
                Stop(startId, "No server to watch");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Attention refresh failed");
        }
    }

    /// <summary>Stops the service unless a later start (a server just added, say) is still to decide.</summary>
    private void Stop(int startId, string why)
    {
        if (StopSelfResult(startId))
            _logger.LogInformation("{Why}; stopping the attention service", why);
        else
            _logger.LogInformation("{Why}, but a later refresh is pending; the attention service keeps running", why);
    }

    private void OnNetworkChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        _logger.LogInformation("Network changed ({Access}); reconnecting attention", e.NetworkAccess);
        _watcher?.Reconnect();
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
