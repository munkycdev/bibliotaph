using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// What pages do with binders: making one through the dialog, and the note a
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

    /// <summary>
    /// One of Add to binder's choices: the binder named, one picked from all of them, or a new one named
    /// first. <paramref name="what"/> names what's added in the picker's heading. Null when cancelled.
    /// </summary>
    public async Task<ActionResult?> AddAsync(BinderMenu menu, BinderMenuChoice choice, string what, BinderAdd add)
    {
        long? id = choice.Kind switch
        {
            BinderMenuKind.Binder => choice.Id,
            BinderMenuKind.Choose => (await dialogs.PickAsync(BinderMenu.PickHeading(what), menu.All))?.Id,
            _ => await CreateAsync(menu.Systems, menu.NewHeading, menu.NewAction),
        };
        return id is { } binder ? await api.AddToBinderAsync(binder, add) : null;
    }

    /// <summary>A search hit's Add: to the binder worked on last, or a new one when there's none yet.</summary>
    public async Task<ActionResult?> AddToCurrentAsync(string what, BinderAdd add)
    {
        var menu = await api.BinderMenuAsync();
        return await AddAsync(menu, menu.Choices[0], what, add);
    }
}
