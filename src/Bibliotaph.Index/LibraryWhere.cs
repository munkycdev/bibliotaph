using System.Text;
using System.Text.Json;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Dapper;

namespace Bibliotaph.Index;

/// <summary>
/// The WHERE clause shared by the library and both tabs, over the cards <c>e</c> (<see cref="LibrarySql.Cards"/>) and <c>doc d</c>, the document each
/// entry shows, joined optionally. It always keeps a file's card out until Probe has its document. Built from fixed
/// fragments with parameter names it numbers itself; values go into <see cref="Parameters"/>.
/// </summary>
internal sealed class LibraryWhere
{
    readonly StringBuilder _sql = new("(d.document_id IS NOT NULL OR e.document_id IS NULL)");

    public DynamicParameters Parameters { get; } = new();

    public string Sql => _sql.ToString();

    /// <param name="filter">The scope, format and order the user chose.</param>
    /// <param name="plan">The parsed search; its fields and exclusions become clauses here.</param>
    /// <param name="titleAsFilter">
    /// Apply title: as a document filter: for the library and page search. The Documents tab has it in its MATCH instead.
    /// </param>
    public LibraryWhere(LibraryFilter filter, SearchPlan plan, bool titleAsFilter)
    {
        if (filter.Scope is { } scope) Add("e.entry_id IN (SELECT value FROM json_each(@scope))", "scope", JsonSerializer.Serialize(scope));
        if (filter.Group is { } group) Add("e.entry_id IN (SELECT entry_id FROM entry_scope WHERE scope = @group)", "group", group);
        if (filter.OnlyOpened) _sql.Append(" AND e.entry_id IN (SELECT entry_id FROM entry_opened)");
        for (var i = 0; i < plan.Scopes.Count; i++)
        {
            var (inScope, negated) = (plan.Scopes[i], plan.Scopes[i].Negated ? "NOT IN" : "IN");
            // A collection's scopes (whole and own) share its name; only the whole one, sub-collections included, matches by name.
            if (!inScope.ByName) Add($"e.entry_id {negated} (SELECT entry_id FROM entry_scope WHERE scope = @inScope{i})", $"inScope{i}", inScope.Scope);
            else if (inScope.Prefix)
                Add($"""e.entry_id {negated} (SELECT es.entry_id FROM entry_scope es JOIN scope_name sn ON sn.scope = es.scope WHERE sn.scope LIKE 'collection:%' AND sn.name LIKE @inScope{i} ESCAPE '\')""",
                    $"inScope{i}", Escape(inScope.Scope) + "%");
            else
                Add($"e.entry_id {negated} (SELECT es.entry_id FROM entry_scope es JOIN scope_name sn ON sn.scope = es.scope WHERE sn.scope LIKE 'collection:%' AND sn.name = @inScope{i} COLLATE NOCASE)",
                    $"inScope{i}", inScope.Scope);
        }

        switch (filter.Kind)
        {
            case KindFilter.Books:
                Add("e.kind <> 'Pack' AND d.format IN (SELECT value FROM json_each(@bookFormats))", "bookFormats", JsonSerializer.Serialize(SourceFormats.Paged));
                break;
            case KindFilter.Images:
                Add("e.kind <> 'Pack' AND d.format IN (SELECT value FROM json_each(@imageFormats))", "imageFormats", JsonSerializer.Serialize(SourceFormats.Images));
                break;
            case KindFilter.Packs:
                // While each file has a card of its own, Packs keeps the files in packs.
                _sql.Append(filter.PackFiles ? " AND e.pack_file IS NOT NULL" : " AND e.kind = 'Pack'");
                break;
            case KindFilter.Here:
                _sql.Append(" AND e.kind <> 'Elsewhere'");
                break;
            case KindFilter.Elsewhere:
                _sql.Append(" AND e.kind = 'Elsewhere'");
                break;
        }

        foreach (var where in plan.Wheres) _sql.Append(where.Elsewhere ? " AND e.kind = 'Elsewhere'" : " AND e.kind <> 'Elsewhere'");
        foreach (var opened in plan.Opened) _sql.Append($" AND e.entry_id {(opened.Opened ? "IN" : "NOT IN")} (SELECT entry_id FROM entry_opened)");

        if (filter.OnlyWithCopies) _sql.Append(" AND e.copies > 1");
        if (filter.HideParts) _sql.Append(" AND e.kind <> 'Part'");
        // Kept inside their books, adventures show only when a search finds them; browsing lists them in their files.
        else if (filter.PartsInBooks && plan.IsEmpty) _sql.Append(" AND e.kind <> 'Part'");

        // A collection's whole scope is "collection:<id>"; its own-books scope ("collection-own:") is the same books or fewer.
        if (filter.Collections != CollectionFilter.All)
            _sql.Append($" AND e.entry_id {(filter.Collections == CollectionFilter.InOne ? "IN" : "NOT IN")} (SELECT entry_id FROM entry_scope WHERE scope LIKE 'collection:%')");
        if (filter.Ai != AiFilter.All)
            _sql.Append($" AND e.entry_id {(filter.Ai == AiFilter.Read ? "IN" : "NOT IN")} (SELECT entry_id FROM entry_ai)");

        if (plan.Formats.Count > 0) Add("d.format IN (SELECT value FROM json_each(@formats))", "formats", JsonSerializer.Serialize(plan.Formats));
        if (plan.ExcludedFormats.Count > 0)
            Add("coalesce(d.format, '') NOT IN (SELECT value FROM json_each(@excludedFormats))", "excludedFormats", JsonSerializer.Serialize(plan.ExcludedFormats));

        for (var i = 0; i < plan.Folders.Count; i++)
            Add($"coalesce(d.folder_hint, '') LIKE @folder{i} ESCAPE '\\'", $"folder{i}", Contains(plan.Folders[i]));
        for (var i = 0; i < plan.ExcludedFolders.Count; i++)
            Add($"coalesce(d.folder_hint, '') NOT LIKE @excludedFolder{i} ESCAPE '\\'", $"excludedFolder{i}", Contains(plan.ExcludedFolders[i]));

        for (var i = 0; i < plan.Isbns.Count; i++)
        {
            var isbn = plan.Isbns[i];
            // A book owned elsewhere has no document, so no ISBN of its own: it is one of the unknown.
            var found = isbn.IsUnknown
                ? "coalesce(e.document_id, -1) IN (SELECT document_id FROM doc_identifier WHERE kind = 'Isbn')"
                : $"coalesce(e.document_id, -1) IN (SELECT document_id FROM doc_identifier WHERE kind = 'Isbn' AND value = @isbn{i})";
            Add(isbn.Negated != isbn.IsUnknown ? $"NOT {found}" : found, $"isbn{i}", isbn.Value);
        }

        // Only set when there are no words to find, so it never applies to page search (which then finds nothing).
        if (plan.TextExclude is { } textExclude)
            Add("e.entry_id NOT IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @textExclude)", "textExclude", textExclude);
        if (titleAsFilter && plan.FieldMatch is { } fields)
            Add("e.entry_id IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @fieldMatch)", "fieldMatch", fields);
        if (plan.FieldExclude is { } fieldExclude)
            Add("e.entry_id NOT IN (SELECT rowid FROM entry_fts WHERE entry_fts MATCH @fieldExclude)", "fieldExclude", fieldExclude);

        for (var i = 0; i < plan.Facets.Count; i++) AddFacet(plan.Facets[i], i);
        for (var i = 0; i < plan.Levels.Count; i++) AddLevel(plan.Levels[i], i);
        for (var i = 0; i < plan.Players.Count; i++) AddPlayers(plan.Players[i], i);

        if (filter.Systems is { Count: > 0 } systems) AddChoice("system", systems, "systems");
        if (filter.Types is { Count: > 0 } types) AddChoice("type", types, "types");
        if (filter.Owns is { Count: > 0 } owns) AddChoice("own", owns, "owns");
        if (filter.Styles is { Count: > 0 } styles) AddChoice("style", styles, "styles");
        if (filter.Durations is { Count: > 0 } durations) AddChoice("duration", durations, "durations");
        if (filter.Stores is { Count: > 0 } stores) AddChoice("store", stores, "stores");
        if (filter.Found is { Count: > 0 } foundBy) AddChoice(FoundOnline.Field, foundBy, "found");
        if (filter.Picked is { } picked)
        {
            var i = 0;
            foreach (var field in picked.Fields) AddPicked(field, picked[field], i++);
        }
        if (filter.Players is { } players)
            Add("EXISTS (SELECT 1 FROM entry_meta pm WHERE pm.entry_id = e.entry_id AND pm.players_min <= @players AND pm.players_max >= @players)",
                "players", players);
        if (filter.Level is { } level)
        {
            var known = "EXISTS (SELECT 1 FROM entry_meta lm WHERE lm.entry_id = e.entry_id AND lm.level_state = 'known' AND lm.level_min <= @level AND lm.level_max >= @level)";
            Add(filter.IncludeUnknownLevel
                ? $"({known} OR NOT EXISTS (SELECT 1 FROM entry_meta lm WHERE lm.entry_id = e.entry_id AND lm.level_state <> 'unknown'))"
                : known, "level", level);
        }
    }

    /// <summary>Entries with any of <paramref name="values"/> for <paramref name="field"/>; unknown picks those with none.</summary>
    void AddChoice(string field, IReadOnlyCollection<string> values, string name)
    {
        var known = values.Where(v => v != SearchQuery.Unknown).ToList();
        var clauses = new List<string>();
        if (known.Count > 0)
            clauses.Add($"e.entry_id IN (SELECT entry_id FROM entry_facet WHERE field = @{name}Field AND value IN (SELECT value FROM json_each(@{name})))");
        if (known.Count < values.Count)
            clauses.Add($"e.entry_id NOT IN (SELECT entry_id FROM entry_facet WHERE field = @{name}Field)");
        Add("(" + string.Join(" OR ", clauses) + ")", name + "Field", field);
        Parameters.Add(name, JsonSerializer.Serialize(known));
    }

    /// <summary>
    /// Entries with any of the values picked in a field's type-ahead filter: a publisher by its name, anything else as
    /// a term.
    /// </summary>
    void AddPicked(string field, IReadOnlyList<string> values, int i)
    {
        if (field != MetadataFields.Publisher.Key)
        {
            AddChoice(field, values, $"picked{i}");
            return;
        }
        Add($"e.entry_id IN (SELECT entry_id FROM entry_meta WHERE publisher IN (SELECT value FROM json_each(@picked{i})))",
            $"picked{i}", JsonSerializer.Serialize(values));
    }

    /// <summary>
    /// <c>type:adventure</c>: a value matches a term through any of its names (term_alias), its label, or its key;
    /// a prefix (<c>type:adv*</c>) through the start of its label or names.
    /// </summary>
    void AddFacet(FacetCondition facet, int i)
    {
        var fields = $"(SELECT value FROM json_each(@facetFields{i}))";
        if (facet.IsUnknown)
        {
            // A book with no setting of its own is still unknown, whatever its chapters have: unknown asks what the
            // book is, and a field found only in chapters (creatures) asks for books with none found.
            var own = facet.Fields.Where(f => !f.StartsWith(ChapterFields.InsidePrefix, StringComparison.Ordinal)).ToList();
            Parameters.Add($"facetFields{i}", JsonSerializer.Serialize(own.Count > 0 ? own : facet.Fields));
            _sql.Append(" AND e.entry_id ").Append(facet.Negated ? "IN" : "NOT IN")
                .Append($" (SELECT entry_id FROM entry_facet WHERE field IN {fields})");
            return;
        }
        Parameters.Add($"facetFields{i}", JsonSerializer.Serialize(facet.Fields));
        var normalized = MetadataText.Normalize(facet.Value);
        // A chapter's value is a term of the same vocabulary as the book's field it is filed beside.
        var vocabulary = $"(CASE WHEN f.field LIKE '{ChapterFields.InsidePrefix}%' THEN substr(f.field, {ChapterFields.InsidePrefix.Length + 1}) ELSE f.field END)";
        var matches = facet.Prefix
            ? $"f.label LIKE @facetLike{i} ESCAPE '\\' OR f.value IN (SELECT a.value FROM term_alias a WHERE a.vocabulary = {vocabulary} AND a.alias LIKE @facetLike{i} ESCAPE '\\')"
            : $"f.value = @facetKey{i} OR f.label LIKE @facetLike{i} ESCAPE '\\' OR f.value IN (SELECT a.value FROM term_alias a WHERE a.vocabulary = {vocabulary} AND a.alias = @facetAlias{i})";
        Add($"e.entry_id {(facet.Negated ? "NOT IN" : "IN")} (SELECT f.entry_id FROM entry_facet f WHERE f.field IN {fields} AND ({matches}))",
            $"facetLike{i}", facet.Prefix ? Escape(normalized) + "%" : Contains(facet.Value));
        Parameters.Add($"facetKey{i}", facet.Value.ToLowerInvariant());
        Parameters.Add($"facetAlias{i}", normalized);
    }

    /// <summary><c>level:3</c> and <c>level:2-4</c> match books whose range overlaps; none and unknown match those states.</summary>
    void AddLevel(LevelCondition level, int i)
    {
        string condition;
        if (level.Value == SearchQuery.Unknown) condition = "lm.level_state <> 'unknown'";
        else if (!LevelRange.TryParse(level.Value, out var range)) return;
        else if (range.NotApplicable) condition = "lm.level_state = 'na'";
        else
        {
            condition = $"lm.level_state = 'known' AND lm.level_min <= @levelMax{i} AND lm.level_max >= @levelMin{i}";
            Parameters.Add($"levelMin{i}", range.Min);
            Parameters.Add($"levelMax{i}", range.Max);
        }
        // Unknown is the absence of a known state, so level:unknown is "no known or n/a levels".
        var exists = level.Value == SearchQuery.Unknown ? !level.Negated : level.Negated;
        _sql.Append(" AND ").Append(exists ? "NOT EXISTS" : "EXISTS")
            .Append($" (SELECT 1 FROM entry_meta lm WHERE lm.entry_id = e.entry_id AND {condition})");
    }

    /// <summary><c>players:4</c> and <c>players:3-5</c> match books whose range overlaps; unknown matches books with none.</summary>
    void AddPlayers(PlayersCondition players, int i)
    {
        string condition;
        if (players.Value == SearchQuery.Unknown) condition = "pm.players_min IS NOT NULL";
        else if (!PlayerRange.TryParse(players.Value, out var range)) return;
        else
        {
            condition = $"pm.players_min <= @playersMax{i} AND pm.players_max >= @playersMin{i}";
            Parameters.Add($"playersMin{i}", range.Min);
            Parameters.Add($"playersMax{i}", range.Max);
        }
        var exists = players.Value == SearchQuery.Unknown ? !players.Negated : players.Negated;
        _sql.Append(" AND ").Append(exists ? "NOT EXISTS" : "EXISTS")
            .Append($" (SELECT 1 FROM entry_meta pm WHERE pm.entry_id = e.entry_id AND {condition})");
    }

    /// <summary>One more clause, for a caller that needs to narrow the set further.</summary>
    public void Also(string clause, string name, object value) => Add(clause, name, value);

    void Add(string clause, string name, object value)
    {
        _sql.Append(" AND ").Append(clause);
        Parameters.Add(name, value);
    }

    /// <summary>A LIKE pattern for "contains", with LIKE's own wildcards escaped.</summary>
    static string Contains(string value) => "%" + Escape(value) + "%";

    static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
