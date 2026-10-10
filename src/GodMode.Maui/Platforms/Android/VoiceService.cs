using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using GodMode.Maui.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>
/// The foreground service (type microphone) that runs while voice is on, so the app keeps hearing the user with the
/// screen off or another app in front. It holds no session: <see cref="VoiceHost"/>, the app's, does, and a recreated
/// activity finds it there. The service keeps the process in the foreground and the microphone allowed.
/// It is not <see cref="AttentionService"/>: that one lives while notifications are on and a server is watched, is
/// restarted by the system after it is killed, and may start without the app in front; this one lives exactly from
/// voice.start to voice.stop, can only start from the app in front, and has nothing to come back to after a kill.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ServiceType)]
public sealed class VoiceService : Service
{
#pragma warning disable CA1416 // Android 10 named the type; below 10 there are none
    private const ForegroundService ServiceType = ForegroundService.TypeMicrophone;
#pragma warning restore CA1416

    private const string Channel = "voice";
    private const int NotificationId = 200;
    private const string ActionStart = "com.godmode.maui.voice.START";
    private const string ActionStop = "com.godmode.maui.voice.STOP";

    /// <summary>How long a start waits for the service to be in the foreground (Android allows it 5 s).</summary>
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(5);

    private static TaskCompletionSource? _started;

    /// <summary>
    /// Starts the service and waits until it is in the foreground, so the microphone stays allowed once the app leaves the
    /// screen. Call it from the app in front, with RECORD_AUDIO granted: Android 14 refuses a microphone service otherwise.
    /// </summary>
    public static async Task StartAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _started = started;
        var context = Platform.AppContext;
        var intent = new Intent(context, typeof(VoiceService)).SetAction(ActionStart);
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
        }
        catch (Exception ex)
        {
            // ForegroundServiceStartNotAllowedException: the app was not in front after all
            throw new InvalidOperationException($"Voice could not start its service: {ex.Message}", ex);
        }
        try
        {
            await started.Task.WaitAsync(StartWait);
        }
        catch (TimeoutException)
        {
            Stop();
            throw new InvalidOperationException("Voice could not start its service: Android did not start it in time");
        }
    }

    public static void Stop()
    {
        var context = Platform.AppContext;
        context.StopService(new Intent(context, typeof(VoiceService)));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var logger = MauiProgram.LoggerFactory.CreateLogger<VoiceService>();
        if (intent?.Action == ActionStop)
        {
            // The notification's Stop: voice stops as if the page asked, and that stops this service. One that Android
            // made again for the Stop, with no session to stop, would stay started with no notification: it stops itself
            logger.LogInformation("Voice stopped from its notification");
            _ = StopVoiceAsync();
            return StartCommandResult.NotSticky;
        }

        var started = Interlocked.Exchange(ref _started, null);
        try
        {
            CreateChannel();
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(NotificationId, BuildNotification(), ServiceType);
            else
                StartForeground(NotificationId, BuildNotification());
            logger.LogInformation("Voice service in the foreground");
            started?.TrySetResult();
        }
        catch (Exception ex)
        {
            // SecurityException on Android 14 without RECORD_AUDIO, or started from the background
            logger.LogWarning("Voice service could not go to the foreground: {Error}", ex.Message);
            started?.TrySetException(new InvalidOperationException(ex is Java.Lang.SecurityException
                ? "Android lets voice start only while GodMode is on screen, with the microphone allowed"
                : $"Voice could not start its service: {ex.Message}", ex));
            StopSelf();
        }
        // After a kill the session is gone with the process, and a microphone service may not start from the background
        return StartCommandResult.NotSticky;
    }

    private async Task StopVoiceAsync()
    {
        try
        {
            await MauiProgram.Services.GetRequiredService<VoiceHost>().StopAsync();
        }
        finally
        {
            StopSelf();
        }
    }

    public override void OnDestroy()
    {
        MauiProgram.LoggerFactory.CreateLogger<VoiceService>().LogInformation("Voice service destroyed");
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var builder = new NotificationCompat.Builder(this, Channel);
        builder.SetSmallIcon(Resource.Drawable.ic_voice);
        builder.SetContentTitle("Voice is on");
        builder.SetContentText("Listening for you, also with the screen off");
        builder.SetOngoing(true);
        builder.SetSilent(true);
        builder.SetCategory(NotificationCompat.CategoryService);
        builder.SetContentIntent(PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable));
        var stop = PendingIntent.GetService(this, 0, new Intent(this, typeof(VoiceService)).SetAction(ActionStop), PendingIntentFlags.Immutable)!;
        builder.AddAction(0, "Stop", stop);
        return builder.Build()!;
    }

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(Channel, "Voice", NotificationImportance.Low)
        {
            Description = "Shown while GodMode listens for you",
        };
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
    }
}
