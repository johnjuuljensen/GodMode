using VoiceBot.Core.AI;

namespace GodMode.Voice.Tests;

/// <summary>
/// The model the control node talks to (#379): Sonnet, not Haiku, which got commands and facts wrong. Under the
/// default settings, through the tier map the cloud providers give VoiceBot's router.
/// </summary>
public sealed class ControlModelTests
{
    [Fact]
    public async Task The_control_chat_runs_on_Sonnet_by_default()
    {
        var model = new ScriptedChatClient().CallTool(VoiceTools.WhatNeedsMe);
        var tiered = new TieredModel(model);
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model, inference: tiered);

        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync("Intet venter.");

        var tierMap = CloudVoiceProviders.TierMap();
        Assert.Equal([VoiceBot.AI.TierMapConfiguration.DefaultModels[VoiceBot.Core.AI.InferenceTier.Medium]], tiered.Tiers.Select(t => tierMap[t].Model).Distinct());
    }
}
