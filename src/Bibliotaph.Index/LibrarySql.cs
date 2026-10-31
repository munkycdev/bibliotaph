
namespace Bibliotaph.Index;

/// <summary>
/// The SQL fragments every card query is built from: the cards themselves, their columns, the joins they need, and the
/// orders they come in. Fixed text only; values are bound by the queries that use them.
/// </summary>
internal static class LibrarySql
{
    /// <summary>Column weights for entry_fts, in schema order: title, subtitle, publisher, series, authors, tags, notes, confirmed, provisional.</summary>
    public const string DocRank = "bm25(entry_fts, 10.0, 5.0, 2.0, 3.0, 2.0, 3.0, 1.0, 4.0, 1.0)";

    /// <summary>A file's name in a pack weighs as a title does: you look for "kraken", not for the pack's name.</summary>
    public const string MemberRank = "bm25(member_fts, 10.0)";

    /// <summary>A card's title: its effective title, or the one from its file name; a file in a pack on a card of its own has its file's.</summary>
    public const string Title = "coalesce(CASE WHEN e.pack_file IS NOT NULL THEN d.display_title END, m.title, e.name, d.display_title, '')";

    public const string EntryColumns = $"""
        e.entry_id AS EntryId, coalesce(e.document_id, 0) AS DocumentId, {Title} AS Title, coalesce(d.format, '') AS Format,
        CASE WHEN e.kind = 'Part' THEN e.last_page - e.first_page + 1 ELSE d.page_count END AS PageCount,
        CASE WHEN e.kind = 'Part' THEN coalesce(
            (SELECT pc.cover FROM page_cover pc WHERE pc.document_id = e.document_id AND pc.pdf_page = e.first_page),
            CASE WHEN e.first_page = 0 THEN d.cover END) ELSE coalesce(cvd.cover, d.cover, e.cover) END AS Cover,
        d.folder_hint AS FolderHint, coalesce(d.added_utc, e.added_utc, '') AS AddedUtc,
        coalesce(s.status IN ('Complete', 'Partial', 'Skipped'), 0) AS Searchable, s.status AS TextStatus, s.reason AS TextReason,
        ps.status AS ProbeStatus, ps.reason AS ProbeReason,
        m.system_label AS SystemLabel, m.kind_label AS KindLabel, m.publisher AS Publisher, m.level_min AS LevelMin, m.level_max AS LevelMax,
        coalesce(m.level_state, 'unknown') AS LevelState, m.players_min AS PlayersMin, m.players_max AS PlayersMax,
        coalesce(m.needs_review, 0) AS NeedsReview, coalesce(m.suggested, 0) AS Suggested,
        ai.model AS AiModel, e.copies AS Copies, e.kind AS EntryKind, e.members AS Members,
        CASE WHEN e.kind = 'Pack' AND cvd.cover IS NULL THEN (
            SELECT json_group_array(cover) FROM (
                SELECT md.cover FROM entry_member em JOIN doc md ON md.document_id = em.document_id
                WHERE em.entry_id = e.entry_id AND md.cover IS NOT NULL ORDER BY em.ord LIMIT 4)) END AS MemberCovers,
        CASE WHEN cvd.cover IS NOT NULL THEN e.cover_member END AS CoverMember,
        CASE WHEN e.kind = 'Pack' THEN (
            SELECT count(*) FROM entry_member em JOIN doc md ON md.document_id = em.document_id
            WHERE em.entry_id = e.entry_id AND md.format = 'pdf') ELSE 0 END AS PdfMembers,
        (SELECT group_concat(label, ' · ') FROM (SELECT ef.label FROM entry_facet ef WHERE ef.entry_id = e.entry_id AND ef.field = 'own' ORDER BY ef.label)) AS AlsoOwn,
        (SELECT ol.link FROM entry_facet ol WHERE ol.entry_id = e.entry_id AND ol.field = 'own' AND ol.link IS NOT NULL ORDER BY ol.label LIMIT 1) AS OnlineLink,
        (SELECT ol.label FROM entry_facet ol WHERE ol.entry_id = e.entry_id AND ol.field = 'own' AND ol.link IS NOT NULL ORDER BY ol.label LIMIT 1) AS OnlineLabel,
        EXISTS (SELECT 1 FROM entry_scope fs WHERE fs.scope = 'favorite' AND fs.entry_id = e.entry_id) AS Favorite, o.opened_utc AS OpenedUtc,
        e.parent_id AS ParentId, e.first_page AS FirstPage, e.last_page AS LastPage, e.parts AS Parts,
        CASE WHEN e.parent_id IS NOT NULL THEN (
            SELECT coalesce(pm.title, pe.name, pd.display_title) FROM entry_doc pe
            LEFT JOIN entry_meta pm ON pm.entry_id = pe.entry_id LEFT JOIN doc pd ON pd.document_id = pe.document_id
            WHERE pe.entry_id = e.parent_id) END AS ParentTitle,
        CASE WHEN e.kind = 'Part' THEN (SELECT fp.label FROM page fp WHERE fp.document_id = e.document_id AND fp.pdf_page = e.first_page) END AS FirstLabel,
        CASE WHEN e.kind = 'Part' THEN (SELECT lp.label FROM page lp WHERE lp.document_id = e.document_id AND lp.pdf_page = e.last_page) END AS LastLabel,
        CASE WHEN e.kind = 'Part' THEN d.cover END AS FileCover,
        e.pack_file AS PackFile, CASE WHEN e.pack_file IS NOT NULL THEN coalesce(m.title, e.name) END AS PackTitle
        """;

    /// <summary>
    /// Library cards: each entry with the document it shows, or none for a book owned elsewhere. <see cref="LibraryWhere"/>
    /// leaves out a file's card until Probe has its document.
    /// </summary>
    public static string Entries(bool packFiles) => $"{Cards(packFiles)} e LEFT JOIN doc d ON d.document_id = e.document_id";

    /// <summary>
    /// The cards, as <c>e</c>: entry_doc, with <c>pack_file</c> null. With <paramref name="packFiles"/>, each pack's card
    /// gives way to a card per file: the pack's entry and name, the file's document and its own entry as
    /// <c>pack_file</c>. Keeping the pack's entry is what lets every filter, field and mark treat a file as its pack.
    /// </summary>
    public static string Cards(bool packFiles) => packFiles
        ? """
            (SELECT entry_id, document_id, kind, copies, name, members, added_utc, parent_id, first_page, last_page, parts, cover, cover_member,
                    NULL AS pack_file
             FROM entry_doc WHERE kind <> 'Pack'
             UNION ALL
             SELECT p.entry_id, em.document_id, 'Whole', 1, p.name, 0, p.added_utc, NULL, NULL, NULL, 0, NULL, NULL, em.member_entry_id
             FROM entry_doc p JOIN entry_member em ON em.entry_id = p.entry_id
             WHERE p.kind = 'Pack')
            """
        : "(SELECT *, NULL AS pack_file FROM entry_doc)";

    public const string EntryJoin = """
        LEFT JOIN stage_status s ON s.document_id = d.document_id AND s.stage = 'Text'
        LEFT JOIN stage_status ps ON ps.document_id = d.document_id AND ps.stage = 'Probe'
        LEFT JOIN entry_meta m ON m.entry_id = e.entry_id
        LEFT JOIN entry_ai ai ON ai.entry_id = e.entry_id
        LEFT JOIN entry_opened o ON o.entry_id = e.entry_id
        LEFT JOIN entry_member cvm ON cvm.entry_id = e.entry_id AND cvm.member_entry_id = e.cover_member
        LEFT JOIN doc cvd ON cvd.document_id = cvm.document_id
        """;

    /// <summary>
    /// The query for the cards <paramref name="where"/> keeps, after any common table expressions <paramref
    /// name="with"/> needs, in <paramref name="order"/> and at most @limit of them when <paramref name="limit"/> is set.
    /// With <paramref name="filesGiveWay"/>, a file one of whose adventures is found too is left out: the adventure is
    /// what was looked for, and its card shows the file's cover.
    /// </summary>
    public static string Found(string? with, string columns, string from, string where, string order, bool limit, bool filesGiveWay)
    {
        var top = limit ? "LIMIT @limit" : "";
        if (!filesGiveWay)
            return $"""
                {(with is null ? "" : "WITH " + with)}
                SELECT {columns}
                FROM {from}
                WHERE {where}
                ORDER BY {order}
                {top}
                """;
        // The order is kept as a number, since the files are left out of what was found, sorted, before the limit.
        return $"""
            WITH {(with is null ? "" : with + ",")}
            found AS MATERIALIZED (
                SELECT {columns}, row_number() OVER (ORDER BY {order}) AS FoundOrder
                FROM {from}
                WHERE {where})
            SELECT * FROM found f
            WHERE NOT EXISTS (SELECT 1 FROM found c WHERE c.EntryKind = 'Part' AND c.ParentId = f.EntryId)
            ORDER BY f.FoundOrder
            {top}
            """;
    }

    /// <summary>The order of the cards; the files of one pack, which share its entry, are kept in a steady order by their own.</summary>
    public static string Order(LibrarySort sort, string? relevance) => (sort switch
    {
        LibrarySort.Title => $"{Title} COLLATE NOCASE, e.entry_id",
        LibrarySort.Publisher => $"m.publisher IS NULL, m.publisher COLLATE NOCASE, {Title} COLLATE NOCASE, e.entry_id",
        LibrarySort.RecentlyOpened => "o.opened_utc IS NULL, o.opened_utc DESC, coalesce(d.added_utc, e.added_utc) DESC, e.entry_id DESC",
        LibrarySort.Relevance when relevance is not null => $"{relevance}, e.entry_id",
        _ => "coalesce(d.added_utc, e.added_utc) DESC, e.entry_id DESC",
    }) + ", e.pack_file";
}
