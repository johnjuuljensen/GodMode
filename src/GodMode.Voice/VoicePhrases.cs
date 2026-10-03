using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using VoiceBot.Core.Resources;

namespace GodMode.Voice;

/// <summary>What the bot says itself, not through the model: announcements, the greeting and a create's answer. Danish, else English.</summary>
public sealed class VoicePhrases
{
    private readonly bool _danish;

    public VoicePhrases(SessionLanguages languages) =>
        _danish = languages.Primary.StartsWith("da", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What the session says as it starts listening: "Klar.", and, when there are servers and none answered in time,
    /// that it knows no project yet, so a project it does not know is not taken for one that is not there.
    /// </summary>
    public string Greeting(ServersHeard heard) => (heard.NoneAnswered, _danish) switch
    {
        (false, true) => "Klar.",
        (false, false) => "Ready.",
        (true, true) => "Klar. Ingen server svarer endnu.",
        (true, false) => "Ready. No server answers yet.",
    };

    /// <summary>Before several announcements said together: "3 venter på dig:".</summary>
    public string Several(int count) => _danish ? $"{count} venter på dig:" : $"{count} need you:";

    /// <summary>
    /// A project named alone (#450): its label, then its root and profile, those it is said with: "issue 376 i GodMode,
    /// profil Mega" / "issue 376 in GodMode, profile Mega".
    /// </summary>
    public string Named(SpokenName name) =>
        name.Label + (name.Root is { } root ? _danish ? $" i {root}" : $" in {root}" : "")
        + (name.Profile is { } profile ? _danish ? $", profil {profile}" : $", profile {profile}" : "");

    /// <summary>The project named as a sentence's subject: <see cref="Named"/>, with a comma after a profile, before the verb.</summary>
    private string Subject(SpokenName name) => Named(name) + (name.Profile is null ? "" : ",");

    /// <summary>
    /// One project that needs the user, by its name (<see cref="Named"/>): short, since the model reads the rest when
    /// asked, or, when the session gave its own spoken reply, that reply word for word (<see cref="Spoken"/>).
    /// </summary>
    public string Announce(SpokenName name, AttentionItem item) => Spoken(name, item) ?? (Subject(name), item.Kind, _danish) switch
    {
        (var who, AttentionKind.Question, true) => $"{who} har et spørgsmål",
        (var who, AttentionKind.Question, false) => $"{who} has a question",
        (var who, AttentionKind.Permission, true) => $"{who} skal have tilladelse: {PermissionSummary(item)}. Svar på skærmen",
        (var who, AttentionKind.Permission, false) => $"{who} needs permission: {PermissionSummary(item)}. Answer it on screen",
        (var who, AttentionKind.Error, true) => $"{who} fejlede",
        (var who, AttentionKind.Error, false) => $"{who} failed",
        (var who, AttentionKind.Review, true) => $"{who} har fået ændringsønsker",
        (var who, AttentionKind.Review, false) => $"{who} has changes requested",
        (var who, AttentionKind.Finished, true) => $"{who} er færdig",
        (var who, AttentionKind.Finished, false) => $"{who} is done",
    };

    /// <summary>
    /// The session's own spoken reply (<see cref="AttentionItem.Spoken"/>, issue #384), word for word, after a lead-in
    /// that names the project and what it needs: "issue 283 er færdig: …", "issue 283 spørger: …". The session wrote it,
    /// not the bot, so it never starts the line, where a "Sendt" in it would be the bot's own word (<see cref="SentNode"/>).
    /// Null when the session gave none.
    /// </summary>
    public string? Spoken(SpokenName name, AttentionItem item) => (item.Spoken, item.Kind, _danish) switch
    {
        (null or "", _, _) => null,
        (var spoken, AttentionKind.Question, true) => $"{Subject(name)} spørger: {spoken}",
        (var spoken, AttentionKind.Question, false) => $"{Subject(name)} asks: {spoken}",
        (var spoken, _, true) => $"{Subject(name)} er færdig: {spoken}",
        (var spoken, _, false) => $"{Subject(name)} is done: {spoken}",
    };

    /// <summary>
    /// A create read back, as the question its yes answers: the root, its profile (and server, when there are several),
    /// the action, and what will be made. It holds no yes-word (<see cref="ConfirmCreateNode.HoldsYes"/>), so its echo
    /// can never answer it.
    /// </summary>
    public string ReadBack(CreateRequest request)
    {
        var root = request.Root;
        var where = _danish
            ? $"i {root.Root.Name}, profil {root.Profile}{(request.SeveralServers ? $", server {root.ServerName}" : "")}, som {request.Action.Name}"
            : $"in {root.Root.Name}, profile {root.Profile}{(request.SeveralServers ? $", server {root.ServerName}" : "")}, as {request.Action.Name}";
        var what = (request.Issue, request.Name, request.WithPrompt, _danish) switch
        {
            ({ } issue, _, _, _) => $"issue {issue}",
            (null, { } name, true, true) => $"{name} med beskrivelse",
            (null, { } name, false, true) => $"{name} uden beskrivelse",
            (null, { } name, true, false) => $"{name} with a description",
            (null, { } name, false, false) => $"{name} with no description",
            (null, { } name, null, _) => name,
            (null, null, _, true) => "en session",
            (null, null, _, false) => "a session",
        };
        return _danish ? $"Skal jeg oprette {what} {where}?" : $"Shall I create {what} {where}?";
    }

    /// <summary>Answers went out this turn (<see cref="SentNode"/>): "Sendt til issue 283.", "Sendt til issue 283 og issue 101.".</summary>
    public string Sent(IReadOnlyList<SpokenName> names)
    {
        var distinct = names.Select(Named).Distinct().ToList();
        var to = distinct.Count == 1 ? distinct[0]
            : $"{string.Join(", ", distinct[..^1])} {(_danish ? "og" : "and")} {distinct[^1]}";
        return _danish ? $"Sendt til {to}." : $"Sent to {to}.";
    }

    /// <summary>The model claimed a send, and none went out this turn (<see cref="SentNode"/>).</summary>
    public string NothingSent => _danish ? "Intet sendt. Sig svaret igen." : "Nothing was sent. Say the answer again.";

    /// <summary>A yes after the read-back it would have answered was dropped (it timed out, or the bot said something else).</summary>
    public string NothingToConfirm => _danish ? "Der venter ingen oprettelse. Sig start igen." : "Nothing waits to be created. Say start again.";

    /// <summary>The user said yes to a create read back: it runs, and <see cref="Created"/> says when it is done.</summary>
    public string Creating => _danish ? "Opretter." : "Creating.";

    /// <summary>The user said anything but yes to a create read back.</summary>
    public string CreateCancelled => _danish ? "Annulleret. Intet oprettet." : "Cancelled. Nothing created.";

    /// <summary>A create is done: the new session by its handle, or why it failed.</summary>
    public string Created(CreateOutcome outcome) => (outcome, _danish) switch
    {
        ({ Handle: { } handle }, true) => $"{handle} er oprettet",
        ({ Handle: { } handle }, false) => $"{handle} is created",
        ({ Error: { } error }, true) => $"Oprettelsen i {outcome.Request.Root.Root.Name} fejlede: {error}",
        ({ Error: { } error }, false) => $"Creating in {outcome.Request.Root.Root.Name} failed: {error}",
        (_, true) => $"Færdig i {outcome.Request.Root.Root.Name}",
        (_, false) => $"Done in {outcome.Request.Root.Root.Name}",
    };

    private static string PermissionSummary(AttentionItem item) => item.Permission?.Summary ?? item.Text;
}
