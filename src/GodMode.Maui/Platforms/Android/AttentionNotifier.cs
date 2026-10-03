using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using AndroidX.Core.App;
using GodMode.ClientBase.Attention;
using GodMode.Shared.Enums;
using Microsoft.Extensions.Logging;

namespace GodMode.Maui;

/// <summary>
/// Attention items as Android notifications, one per item, tagged with the item's <see cref="AttentionLink.Key"/> (server
/// and project), grouped under one summary. A tap opens <c>godmode://attention/{key}</c> in <see cref="MainActivity"/>.
/// The item's alert (issue #438) picks the channel: one that interrupts goes on <see cref="InterruptChannel"/> (heads-up,
/// sound), unless this device's sound is off (<see cref="AttentionSound"/>); one that notifies on <see cref="Channel"/>,
/// silent; one for the inbox alone is not posted.
/// </summary>
public sealed class AttentionNotifier(Context context, ILogger logger) : IAttentionNotifier
{
    /// <summary>What notifies: silent, in the shade.</summary>
    public const string Channel = "attention-notify";
    /// <summary>What interrupts: heads-up, with the channel's sound.</summary>
    public const string InterruptChannel = "attention-interrupt";
    /// <summary>The one channel before tiers, high importance, which a channel cannot be turned down from by the app.</summary>
    private const string FormerChannel = "attention";
    private const string Group = "godmode.attention";
    private const int ItemId = 1;
    private const int SummaryId = 2;
    private const string SummaryTag = "summary";

    private readonly NotificationManagerCompat _manager = NotificationManagerCompat.From(context)!;
    private readonly ConcurrentDictionary<string, byte> _shown = new();

    /// <summary>Creates the channels, and drops the one from before tiers; the user can then turn each off or down in the system settings.</summary>
    public static void CreateChannels(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Needs you", NotificationImportance.Low)
        {
            Description = "A session asks a question, waits for a permission, failed or finished",
        });
        manager.CreateNotificationChannel(new NotificationChannel(InterruptChannel, "Important", NotificationImportance.High)
        {
            Description = "An important session needs you",
        });
        manager.DeleteNotificationChannel(FormerChannel);
    }

    public void Show(AttentionNotice notice)
    {
        var item = notice.Item;
        // In the inbox alone: what showed under its key before (another kind, say) goes
        if (item.Alert == AttentionAlert.Inbox)
        {
            logger.LogDebug("Notification {Key}: {Kind} is for the inbox alone", notice.Link.Key, item.Kind);
            if (_shown.ContainsKey(notice.Link.Key) || Showing().Any(link => link.Key == notice.Link.Key)) Cancel(notice.Link);
            return;
        }
        var interrupts = item.Alert == AttentionAlert.Interrupt && AttentionSound.Enabled;
        var builder = new NotificationCompat.Builder(context, interrupts ? InterruptChannel : Channel);
        builder.SetSmallIcon(Resource.Drawable.ic_attention);
        // A question says it is one, and expanded it reads in full: every question, its options and their
        // descriptions (#454). What does not fit, a tap opens in the app
        builder.SetContentTitle(AttentionNotificationText.Title(item));
        builder.SetContentText(AttentionNotificationText.Line(item));
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(AttentionNotificationText.Body(item)));
        builder.SetSubText(notice.ServerName);
        builder.SetWhen(new DateTimeOffset(DateTime.SpecifyKind(item.Since, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        builder.SetShowWhen(true);
        builder.SetCategory(NotificationCompat.CategoryMessage);
        builder.SetPriority(interrupts ? NotificationCompat.PriorityHigh : NotificationCompat.PriorityLow);
        builder.SetGroup(Group);
        builder.SetAutoCancel(true);
        builder.SetContentIntent(OpenIntent(notice.Link));
        // A changed item alerts again. One this process has not shown yet alerts only if nothing shows under its
        // key: after a restart, the items still showing from before stay quiet
        builder.SetOnlyAlertOnce(!_shown.ContainsKey(notice.Link.Key));
        logger.LogDebug("Notification {Key}: {Kind}, {Alert}", notice.Link.Key, item.Kind, interrupts ? "interrupts" : "notifies");
        Notify(notice.Link.Key, ItemId, builder.Build()!);
        _shown[notice.Link.Key] = 0;
        UpdateSummary();
    }

    public void Cancel(AttentionLink link)
    {
        logger.LogDebug("Notification {Key} cancelled", link.Key);
        _manager.Cancel(link.Key, ItemId);
        _shown.TryRemove(link.Key, out _);
        UpdateSummary(link.Key);
    }

    /// <summary>The items shown now, by this process or by one the system ended without stopping the service.</summary>
    public IReadOnlyCollection<AttentionLink> Showing() =>
        [.. ((NotificationManager)context.GetSystemService(Context.NotificationService)!).GetActiveNotifications()!
            .Where(n => n.Id == ItemId)
            .Select(n => AttentionLink.FromKey(n.Tag))
            .OfType<AttentionLink>()];

    /// <summary>Removes everything this notifier showed (the service is stopping and can no longer keep it right).</summary>
    public void CancelAll()
    {
        foreach (var key in _shown.Keys)
            _manager.Cancel(key, ItemId);
        _shown.Clear();
        UpdateSummary();
    }

    /// <param name="cancelled">The key just cancelled, which the system may still list for a moment.</param>
    private void UpdateSummary(string? cancelled = null)
    {
        var count = _shown.Count;
        if (count == 0)
        {
            // Cancelling a group's summary cancels every notification in the group, even one posted a moment ago:
            // leave it while anything else of ours shows (what an earlier run left, until the tracker reaches it)
            if (Showing().Any(l => l.Key != cancelled)) return;
            logger.LogDebug("Summary cancelled");
            _manager.Cancel(SummaryTag, SummaryId);
            return;
        }
        var builder = new NotificationCompat.Builder(context, Channel);
        builder.SetSmallIcon(Resource.Drawable.ic_attention);
        builder.SetContentTitle(count == 1 ? "1 session needs you" : $"{count} sessions need you");
        builder.SetGroup(Group);
        builder.SetGroupSummary(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetSilent(true);
        builder.SetContentIntent(OpenIntent(null));
        Notify(SummaryTag, SummaryId, builder.Build()!);
    }

    /// <summary>Opens the app on the item; with no item, just opens it.</summary>
    private PendingIntent OpenIntent(AttentionLink? link)
    {
        var intent = new Intent(context, typeof(MainActivity))
            .SetAction(Intent.ActionView)
            .AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        // The data makes each item's intent distinct, so one tap cannot open another item's link
        if (link != null) intent.SetData(Android.Net.Uri.Parse(link.ToUri().ToString()));
        return PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    private void Notify(string tag, int id, Notification notification)
    {
        // Without POST_NOTIFICATIONS (denied, or revoked in settings) Android drops it; say nothing more here
        if (_manager.AreNotificationsEnabled())
            _manager.Notify(tag, id, notification);
    }
}
