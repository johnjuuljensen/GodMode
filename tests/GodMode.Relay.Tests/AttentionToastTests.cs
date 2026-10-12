using GodMode.ClientBase.Attention;
using GodMode.Shared.Enums;

namespace GodMode.Relay.Tests;

/// <summary>Which attention items a Windows toast shows, and how (#491), apart from the toast itself.</summary>
public sealed class AttentionToastTests
{
    private static readonly string[] Nowhere = [];

    [Fact]
    public void What_interrupts_is_a_banner_with_the_sound()
    {
        Assert.Equal(new AttentionToast(Banner: true, Sound: true),
            AttentionToast.For(AttentionAlert.Interrupt, toastNotify: false, sound: true, Nowhere, foreground: null, showingFromBefore: false));
    }

    [Fact]
    public void With_this_devices_sound_off_what_interrupts_is_a_silent_banner()
    {
        Assert.Equal(new AttentionToast(Banner: true, Sound: false),
            AttentionToast.For(AttentionAlert.Interrupt, toastNotify: false, sound: false, Nowhere, foreground: null, showingFromBefore: false));
    }

    [Fact]
    public void What_interrupts_still_showing_from_an_earlier_run_goes_quietly_into_the_centre()
    {
        Assert.Equal(new AttentionToast(Banner: false, Sound: false),
            AttentionToast.For(AttentionAlert.Interrupt, toastNotify: false, sound: true, Nowhere, foreground: null, showingFromBefore: true));
    }

    [Fact]
    public void What_notifies_is_not_toasted_by_default()
    {
        Assert.Null(AttentionToast.For(AttentionAlert.Notify, toastNotify: false, sound: true, Nowhere, foreground: null, showingFromBefore: false));
    }

    [Fact]
    public void What_notifies_goes_silently_into_the_centre_when_this_device_toasts_it()
    {
        Assert.Equal(new AttentionToast(Banner: false, Sound: false),
            AttentionToast.For(AttentionAlert.Notify, toastNotify: true, sound: true, Nowhere, foreground: null, showingFromBefore: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void What_is_for_the_inbox_alone_is_never_toasted(bool toastNotify)
    {
        Assert.Null(AttentionToast.For(AttentionAlert.Inbox, toastNotify, sound: true, Nowhere, foreground: null, showingFromBefore: false));
    }

    /// <summary>The main window ("") or a profile's, by name without case, as the shell keys its windows.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("Work", "Work")]
    [InlineData("Work", "work")]
    public void The_session_open_in_the_window_in_front_is_not_toasted(string showing, string foreground)
    {
        Assert.Null(AttentionToast.For(AttentionAlert.Interrupt, toastNotify: true, sound: true, [showing], foreground, showingFromBefore: false));
        Assert.Null(AttentionToast.For(AttentionAlert.Notify, toastNotify: true, sound: true, [showing], foreground, showingFromBefore: false));
    }

    /// <summary>Open in a window behind another of the app's, or with another app in front (a minimised window, say).</summary>
    [Theory]
    [InlineData("Work", "")]
    [InlineData("", "Work")]
    [InlineData("Work", null)]
    public void The_session_open_in_a_window_not_in_front_is_toasted(string showing, string? foreground)
    {
        Assert.Equal(new AttentionToast(Banner: true, Sound: true),
            AttentionToast.For(AttentionAlert.Interrupt, toastNotify: false, sound: true, [showing], foreground, showingFromBefore: false));
    }

    [Fact]
    public void Open_in_two_windows_it_is_not_toasted_while_either_is_in_front()
    {
        Assert.Null(AttentionToast.For(AttentionAlert.Interrupt, toastNotify: false, sound: true, ["", "Work"], "Work", showingFromBefore: false));
    }
}
