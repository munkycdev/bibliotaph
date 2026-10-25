using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// What pages do with binders, as the Windows app's SessionActions: making one through the dialog, and the note a
/// page shows after it was left, as after a binder was deleted from its own page.
/// </summary>
public sealed class BinderActions(ApiClient api, BinderDialogs dialogs)
{
    /// <summary>A note for the page shown next, as after a binder was deleted from its own page.</summary>
    public ActionResult? Pending { get; set; }

    /// <summary>New binder: a title and a date, or kept at hand; the new binder's id, or null when cancelled.</summary>
    public async Task<long?> CreateAsync(IReadOnlyList<FilterChoice> systems, string heading = "New binder", string actionText = "Create")
    {
        if (await dialogs.NameAsync(heading, actionText, systems) is not { } named) return null;
        return (await api.CreateBinderAsync(named)).Id;
    }
}
