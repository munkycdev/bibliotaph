using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// One of the user's own tags as Settings > Vocabulary lists it: its name, the words that signal it, and how many books
/// show it because of those words, guessed or kept.
/// </summary>
public sealed record UserTagEntry(long Id, string Name, IReadOnlyList<string> Words, int Books = 0);

/// <summary>
/// What saving a tag did: the tag as saved, and its name before when the save renamed it. <see cref="Problem"/> says why
/// nothing was saved.
/// </summary>
public sealed record UserTagSave(UserTagEntry? Tag, string? OldName = null, string? Problem = null);

/// <summary>
/// The user's own tags in catalog.db, each with the words that signal it. They are the user's work, so they live here
/// rather than in index.db, and a rebuilt index finds them again. The guesses they make are assertions
/// (<see cref="AssertionOrigin.UserTag"/>), stored by <see cref="MetadataStore.ReplaceTagGuessesAsync(string, IReadOnlyList{TagGuess}, CancellationToken)"/>.
/// </summary>
public sealed class UserTagStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Every tag, by name, with the books that show it because of its words.</summary>
    public async Task<IReadOnlyList<UserTagEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tags = await db.UserTags.AsNoTracking().ToListAsync(ct);
        var field = MetadataFields.Tags.Key;
        var books = (await db.Assertions.AsNoTracking()
            .Where(a => a.Origin == AssertionOrigin.UserTag && a.Field == field
                && (a.State == AssertionState.Provisional || a.State == AssertionState.Confirmed))
            .Select(a => new { a.NormalizedValue, a.EntryId })
            .Distinct()
            .ToListAsync(ct))
            .GroupBy(a => a.NormalizedValue, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return [.. tags.Select(t => ToEntry(t, books.GetValueOrDefault(t.NormalizedName)))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Every tag with its words, without counting its books: what checking a newly read book needs.</summary>
    public async Task<IReadOnlyList<UserTagEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.UserTags.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct)).Select(t => ToEntry(t))];
    }

    /// <summary>
    /// Adds a tag (<paramref name="id"/> null) or changes one: its name and the words that signal it, as typed. Refuses a
    /// name with no letters or numbers, or one another tag has, and a tag with no words.
    /// </summary>
    public async Task<UserTagSave> SaveAsync(long? id, string name, string words, CancellationToken ct = default)
    {
        var (tidy, problem) = UserTagRule.ReadName(name);
        if (problem is not null) return new UserTagSave(null, Problem: problem);
        var list = UserTagRule.ReadWords(words);
        if (list.Count == 0) return new UserTagSave(null, Problem: "Give the words that signal this tag, separated by commas.");

        var normalized = MetadataText.Normalize(tidy!);
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.UserTags.AsNoTracking().FirstOrDefaultAsync(t => t.NormalizedName == normalized && t.Id != id, ct) is { } other)
            return new UserTagSave(null, Problem: $"You already have the tag “{other.Name}”.");

        var now = _clock.GetUtcNow().UtcDateTime;
        string? oldName = null;
        UserTag row;
        if (id is { } existing)
        {
            if (await db.UserTags.FirstOrDefaultAsync(t => t.Id == existing, ct) is not { } found)
                return new UserTagSave(null, Problem: "That tag has been deleted.");
            row = found;
            if (row.NormalizedName != normalized) oldName = row.Name;
            row.Name = tidy!;
            row.NormalizedName = normalized;
            row.WordsJson = JsonSerializer.Serialize(list);
            row.UpdatedUtc = now;
        }
        else
        {
            row = new UserTag { Name = tidy!, NormalizedName = normalized, WordsJson = JsonSerializer.Serialize(list), CreatedUtc = now, UpdatedUtc = now };
            db.UserTags.Add(row);
        }
        await db.SaveChangesAsync(ct);
        return new UserTagSave(ToEntry(row), oldName);
    }

    /// <summary>Deletes a tag. Returns it as it was, or null when there was none.</summary>
    public async Task<UserTagEntry?> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.UserTags.FirstOrDefaultAsync(t => t.Id == id, ct) is not { } row) return null;
        db.UserTags.Remove(row);
        await db.SaveChangesAsync(ct);
        return ToEntry(row);
    }

    static UserTagEntry ToEntry(UserTag row, int books = 0) =>
        new(row.Id, row.Name, JsonSerializer.Deserialize<string[]>(row.WordsJson) ?? [], books);
}
