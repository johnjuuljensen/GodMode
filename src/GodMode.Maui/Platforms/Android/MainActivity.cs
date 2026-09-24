using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using GodMode.ClientBase.Attention;
using GodMode.Maui.Bridge;

namespace GodMode.Maui;

[Activity(Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // A recreated activity gets its old intent again; that tap was already handled
        if (savedInstanceState == null) OpenAttentionLink(Intent);
        _ = AttentionService.StartAsync();
    }

    /// <summary>A notification tapped while the app is running (SingleTop brings this activity back).</summary>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenAttentionLink(intent);
    }

    /// <summary>A tapped notification's item (AttentionNotifier) goes to React, which opens it in the inbox.</summary>
    private static void OpenAttentionLink(Intent? intent)
    {
        if (AttentionLink.FromUri(intent?.DataString) is { } link)
            PendingAttentionLink.Set(link);
    }
}
