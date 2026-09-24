using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using AndroidX.Core.App;
using GodMode.ClientBase.Attention;
using GodMode.Shared.Enums;

namespace GodMode.Maui;

/// <summary>
/// Attention items as Android notifications on the <see cref="Channel"/> channel, one per item, tagged with the
/// item's <see cref="AttentionLink.Key"/> (server and project), grouped under one summary. A tap opens
/// <c>godmode://attention/{key}</c> in <see cref="MainActivity"/>.
/// </summary>
public sealed class AttentionNotifier(Context context) : IAttentionNotifier
{
    public const string Channel = "attention";
    private const string Group = "godmode.attention";
    private const int ItemId = 1;
    private const int SummaryId = 2;
    private const string SummaryTag = "summary";

    private readonly NotificationManagerCompat _manager = NotificationManagerCompat.From(context)!;
    private readonly ConcurrentDictionary<string, byte> _shown = new();

    /// <summary>Creates the channel; the user can then turn it off or down in the system settings.</summary>
    public static void CreateChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var channel = new NotificationChannel(Channel, "Needs you", NotificationImportance.High)
        {
            Description = "A session asks a question, waits for a permission, failed or finished",
        };
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(channel);
    }

    public void Show(AttentionNotice notice)
    {
        var item = notice.Item;
        var builder = new NotificationCompat.Builder(context, Channel);
        builder.SetSmallIcon(Resource.Drawable.ic_attention);
        builder.SetContentTitle($"{KindLabel(item.Kind)} · {item.ProjectName}");
        builder.SetContentText(item.Text);
        builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(item.Text));
        builder.SetSubText(notice.ServerName);
        builder.SetWhen(new DateTimeOffset(DateTime.SpecifyKind(item.Since, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        builder.SetShowWhen(true);
        builder.SetCategory(NotificationCompat.CategoryMessage);
        builder.SetPriority(NotificationCompat.PriorityHigh);
        builder.SetGroup(Group);
        builder.SetAutoCancel(true);
        builder.SetContentIntent(OpenIntent(notice.Link));
        // A changed item alerts again. One this process has not shown yet alerts only if nothing shows under its
        // key: after a restart, the items still showing from before stay quiet
        builder.SetOnlyAlertOnce(!_shown.ContainsKey(notice.Link.Key));
        Notify(notice.Link.Key, ItemId, builder.Build()!);
        _shown[notice.Link.Key] = 0;
        UpdateSummary();
    }

    public void Cancel(AttentionLink link)
    {
        _manager.Cancel(link.Key, ItemId);
        _shown.TryRemove(link.Key, out _);
        UpdateSummary();
    }

    /// <summary>Removes everything this notifier showed (the service is stopping and can no longer keep it right).</summary>
    public void CancelAll()
    {
        foreach (var key in _shown.Keys)
            _manager.Cancel(key, ItemId);
        _shown.Clear();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _shown.Count;
        if (count == 0)
        {
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

    private static string KindLabel(AttentionKind kind) => kind switch
    {
        AttentionKind.Permission => "Permission",
        AttentionKind.Question => "Question",
        AttentionKind.Error => "Error",
        AttentionKind.Review => "Changes requested",
        AttentionKind.Finished => "Finished",
    };
}
