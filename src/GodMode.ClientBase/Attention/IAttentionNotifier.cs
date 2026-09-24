using GodMode.Shared.Models;

namespace GodMode.ClientBase.Attention;

/// <summary>An attention item to show, with the server it is on.</summary>
public sealed record AttentionNotice(AttentionLink Link, string ServerName, AttentionItem Item);

/// <summary>
/// Shows attention items to the user outside the app (Android notifications), one per <see cref="AttentionLink.Key"/>.
/// Called from any thread.
/// </summary>
public interface IAttentionNotifier
{
    /// <summary>Shows the item, or updates the one already shown under its key.</summary>
    void Show(AttentionNotice notice);

    /// <summary>Removes what is shown under the link's key, if anything.</summary>
    void Cancel(AttentionLink link);
}
