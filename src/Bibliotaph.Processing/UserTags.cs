using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>What saving a tag did: the tag as saved, with the books that show it now, or why nothing was saved.</summary>
public sealed record UserTagEdit(UserTagEntry? Tag, string? Problem = null);

/// <summary>
/// The user's own tags, found across the library by their words. Saving a tag asks the full-text index which books'
/// pages use its words, and each book that uses them often enough (<see cref="UserTagRule.Qualifies"/>) gets the tag as
/// a guess on Your tags, marked as suggested until the user keeps it. Changing a tag's words or name updates its
/// guesses, deleting it takes them away, and a book read later is checked against every tag once its text is in
/// (<see cref="CheckDocumentAsync"/>). Only undecided guesses change: a guess the user kept stays theirs, and one they
/// rejected is never guessed again.
/// </summary>
public sealed class UserTagService(UserTagStore tags, MetadataStore metadata, UserTagQueries queries, MetadataProjector projector,
    ILogger<UserTagService>? log = null) : IDisposable
{
    readonly ILogger _log = log ?? NullLogger<UserTagService>.Instance;

    /// <summary>
    /// One change to the guesses at a time: a tag saved while a newly read book is being checked would otherwise both
    /// add the same guess.
    /// </summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Every tag, by name, with how many books show it because of its words.</summary>
    public Task<IReadOnlyList<UserTagEntry>> ListAsync(CancellationToken ct = default) => tags.ListAsync(ct);

    /// <summary>
    /// Adds a tag (<paramref name="id"/> null) or changes one, then guesses it across the library. A renamed tag's
    /// undecided guesses under its old name go.
    /// </summary>
    public async Task<UserTagEdit> SaveAsync(long? id, string name, string words, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        UserTagEntry tag;
        var changed = new HashSet<EntryId>();
        try
        {
            var saved = await tags.SaveAsync(id, name, words, ct);
            if (saved.Tag is null) return new UserTagEdit(null, saved.Problem);
            tag = saved.Tag;
            if (saved.OldName is { } old) changed.UnionWith(await metadata.ReplaceTagGuessesAsync(old, [], ct));
            changed.UnionWith(await metadata.ReplaceTagGuessesAsync(tag.Name, await GuessAsync(tag, null, ct), ct));
        }
        finally
        {
            _gate.Release();
        }
        await projector.ProjectAsync(changed, ct);
        var listed = (await tags.ListAsync(ct)).FirstOrDefault(t => t.Id == tag.Id) ?? tag;
        _log.LogInformation("Tag {TagId} saved; {Books} books show it", tag.Id, listed.Books);
        return new UserTagEdit(listed);
    }

    /// <summary>Deletes a tag and its undecided guesses. A book where the user kept it keeps it. False when there was no such tag.</summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        IReadOnlyList<EntryId> changed;
        await _gate.WaitAsync(ct);
        try
        {
            if (await tags.DeleteAsync(id, ct) is not { } tag) return false;
            changed = await metadata.ReplaceTagGuessesAsync(tag.Name, [], ct);
        }
        finally
        {
            _gate.Release();
        }
        await projector.ProjectAsync(changed, ct);
        _log.LogInformation("Tag {TagId} deleted; its guesses left {Books} books", id, changed.Count);
        return true;
    }

    /// <summary>
    /// Guesses again the tags another device added, changed or deleted, by name: a tag that is here now is guessed
    /// across the library, and one that isn't takes its undecided guesses away.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var changed = new HashSet<EntryId>();
        await _gate.WaitAsync(ct);
        try
        {
            var all = await tags.GetAllAsync(ct);
            foreach (var name in names)
            {
                var tag = all.FirstOrDefault(t => MetadataText.Normalize(t.Name) == MetadataText.Normalize(name));
                changed.UnionWith(await metadata.ReplaceTagGuessesAsync(name, tag is null ? [] : await GuessAsync(tag, null, ct), ct));
            }
        }
        finally
        {
            _gate.Release();
        }
        await projector.ProjectAsync(changed, ct);
    }

    /// <summary>
    /// Checks the books showing a document whose text has just been read against every tag, from the pages already in
    /// the index: the tags their pages earn are guessed, and guesses their pages no longer earn go.
    /// </summary>
    public async Task CheckDocumentAsync(long documentId, CancellationToken ct = default)
    {
        IReadOnlyList<EntryId> changed;
        await _gate.WaitAsync(ct);
        try
        {
            var all = await tags.GetAllAsync(ct);
            if (all.Count == 0) return;
            var showing = await queries.GetShowingAsync(documentId, ct);
            if (showing.Count == 0) return;
            var guesses = new List<TagGuess>();
            foreach (var tag in all) guesses.AddRange(await GuessAsync(tag, documentId, ct));
            changed = await metadata.ReplaceTagGuessesAsync(showing, guesses, ct);
        }
        finally
        {
            _gate.Release();
        }
        await projector.ProjectAsync(changed, ct);
    }

    /// <summary>The books, or those showing <paramref name="documentId"/>, whose pages use the tag's words often enough.</summary>
    async Task<IReadOnlyList<TagGuess>> GuessAsync(UserTagEntry tag, long? documentId, CancellationToken ct) =>
        [.. (await queries.FindAsync(tag.Words, documentId, ct))
            .Where(p => UserTagRule.Qualifies(p.Pages.Count, p.TextPages))
            .Select(p => new TagGuess(p.EntryId, tag.Name, p.ContentHash, UserTagRule.Quote(tag.Words), [.. p.Pages.Take(UserTagRule.EvidencePages)]))];
}
