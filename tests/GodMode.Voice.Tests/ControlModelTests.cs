using VoiceBot.Core.AI;

namespace GodMode.Voice.Tests;

/// <summary>
/// The model the control node talks to: the Light tier, Haiku 5.5, for speed (#525). It was Sonnet on the Medium tier
/// (#379), because Haiku 4.5 got commands and facts wrong. Under the default settings, through the tier map the cloud
/// providers give VoiceBot's router.
/// </summary>
public sealed class ControlModelTests
{
    [Fact]
    public async Task The_control_chat_runs_on_the_Light_tier_by_default()
    {
        var model = new ScriptedChatClient().CallTool(VoiceTools.WhatNeedsMe);
        var tiered = new TieredModel(model);
        await using var voice = await OfflineVoice.StartAsync(new FakeServers(), model, inference: tiered);

        voice.Transcriptions.SayAsRecognized("Hvad venter?");
        await voice.Events.SaidAsync("Intet venter.");

        var tierMap = CloudVoiceProviders.TierMap();
        Assert.Equal([InferenceTier.Light], tiered.Tiers.Distinct());
        Assert.Equal([VoiceBot.AI.TierMapConfiguration.DefaultModels[InferenceTier.Light]], tiered.Tiers.Select(t => tierMap[t].Model).Distinct());
        Assert.Equal("claude-haiku-5-5", tierMap[InferenceTier.Light].Model);
    }
}
