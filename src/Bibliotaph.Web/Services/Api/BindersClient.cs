using System.Globalization;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Binders, their sections and items, and Add to binder.</summary>
public sealed class BindersClient(ApiHttp http)
{
    /// <summary>The Binders page's groups, upcoming and earlier as of <paramref name="today"/> where the user is.</summary>
    public Task<BinderList> BindersAsync(DateOnly today, CancellationToken ct = default) =>
        http.SendAsync<BinderList>(HttpMethod.Get, $"api/binders?today={today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", null, ct);

    /// <summary>Add to binder's menu: the binder worked on last first, then another and a new one.</summary>
    public Task<BinderMenu> BinderMenuAsync(CancellationToken ct = default) => http.SendAsync<BinderMenu>(HttpMethod.Get, "api/binders/menu", null, ct);

    public Task<ActionResult> AddToBinderAsync(long id, BinderAdd add, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Binder(id)}/add", add, ct);

    /// <summary>A binder's sections, for Add pages…'s Where.</summary>
    public Task<List<BinderSection>> BinderSectionsAsync(long id, CancellationToken ct = default) =>
        http.SendAsync<List<BinderSection>>(HttpMethod.Get, $"{Binder(id)}/sections", null, ct);

    public Task<BinderCreated> CreateBinderAsync(BinderEdit edit, CancellationToken ct = default) =>
        http.SendAsync<BinderCreated>(HttpMethod.Post, "api/binders", edit, ct);

    /// <summary>Opening a binder's page makes it the current one, which Add to binder adds to.</summary>
    public Task<BinderPage> OpenBinderAsync(long id, CancellationToken ct = default) => http.SendAsync<BinderPage>(HttpMethod.Post, $"{Binder(id)}/open", null, ct);

    public Task<BinderPage> BinderAsync(long id, CancellationToken ct = default) => http.SendAsync<BinderPage>(HttpMethod.Get, Binder(id), null, ct);

    public Task<ActionResult> EditBinderAsync(long id, BinderEdit edit, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Put, Binder(id), edit, ct);

    public Task SetBinderNotesAsync(long id, string notes, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Put, $"{Binder(id)}/notes", new BinderNotes(notes), ct);

    public Task<BinderCreated> DuplicateBinderAsync(long id, CancellationToken ct = default) =>
        http.SendAsync<BinderCreated>(HttpMethod.Post, $"{Binder(id)}/duplicate", null, ct);

    public Task<ActionResult> DeleteBinderAsync(long id, CancellationToken ct = default) => http.SendAsync<ActionResult>(HttpMethod.Delete, Binder(id), null, ct);

    public Task<ActionResult> AddSectionAsync(long id, string name, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"{Binder(id)}/sections", new BinderName(name), ct);

    public Task<ActionResult> RenameSectionAsync(long id, long section, string name, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Put, $"{Binder(id)}/sections/{section.ToString(CultureInfo.InvariantCulture)}", new BinderName(name), ct);

    public Task<ActionResult> DeleteSectionAsync(long id, long section, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Delete, $"{Binder(id)}/sections/{section.ToString(CultureInfo.InvariantCulture)}", null, ct);

    public Task SetBinderItemAsync(long item, string label, string note, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Put, BinderItem(item), new BinderItemEdit(label, note), ct);

    /// <summary>At hand: the pages of the other binders kept at hand, for a binder's reader.</summary>
    public Task<AtHandList> AtHandAsync(long id, CancellationToken ct = default) => http.SendAsync<AtHandList>(HttpMethod.Get, $"{Binder(id)}/at-hand", null, ct);

    /// <summary>Add this page, in a binder's reader: right after the item in hand.</summary>
    public Task<BinderItemAdded> AddBinderPageAsync(long id, BinderPageAdd add, CancellationToken ct = default) =>
        http.SendAsync<BinderItemAdded>(HttpMethod.Post, $"{Binder(id)}/items", add, ct);

    /// <summary>Use this page: the item opens at the page in view from now on.</summary>
    public Task<ReaderItemUsed> UseBinderPageAsync(long item, ReaderUsePage use, CancellationToken ct = default) =>
        http.SendAsync<ReaderItemUsed>(HttpMethod.Post, $"{BinderItem(item)}/use-page", use, ct);

    public Task<bool> MoveBinderItemAsync(long item, BinderItemMove move, CancellationToken ct = default) =>
        http.SendAsync<bool>(HttpMethod.Post, $"{BinderItem(item)}/move", move, ct);

    public Task<ActionResult> RemoveBinderItemAsync(long item, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Delete, BinderItem(item), null, ct);

    static string Binder(long id) => $"api/binders/{id.ToString(CultureInfo.InvariantCulture)}";

    static string BinderItem(long id) => $"api/binders/items/{id.ToString(CultureInfo.InvariantCulture)}";
}
