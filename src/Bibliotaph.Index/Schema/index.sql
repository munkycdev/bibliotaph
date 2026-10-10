-- index.db: everything here is derived from source files and catalog.db, and can be thrown away.
-- To change the schema, change this script and bump IndexSchema.Version (and user_version below). Then either add
-- Schema/upgrade-N.sql, which brings a version N-1 file up to N without losing anything (for additive changes, so an
-- upgrade doesn't re-OCR the library), or don't, and every install deletes and rebuilds index.db on next start;
-- documents re-queue from their hashes.
--
-- document_id values refer to document.id and entry_id values to entry.id in catalog.db (attached at query time, so no
-- foreign keys). Pages, text and processing belong to documents; what the library lists, filters and searches is
-- entries, each shown through its current document.

-- What the Probe stage learned about each document, and its title from the file name.
-- document_id = document.id in catalog.db.
CREATE TABLE doc (
    document_id     INTEGER PRIMARY KEY,
    content_hash    TEXT    NOT NULL,
    format          TEXT    NOT NULL,              -- pdf, jpg, png
    display_title   TEXT    NOT NULL,              -- from the file name, cleaned up
    page_count      INTEGER,
    width_px        INTEGER,                       -- images only
    height_px       INTEGER,
    encrypted       INTEGER NOT NULL DEFAULT 0,
    can_copy        INTEGER NOT NULL DEFAULT 1,
    meta_title      TEXT,                          -- the PDF's own document information, as provisional text
    meta_author     TEXT,
    meta_subject    TEXT,
    meta_keywords   TEXT,
    folder_hint     TEXT,                          -- folder names above the file, for search
    cover           TEXT,                          -- cover file name in the cache folder, once made
    added_utc       TEXT    NOT NULL
);

-- Bookmarks, flattened in reading order. pdf_page is -1 for a bookmark that points outside the file.
CREATE TABLE outline (
    document_id  INTEGER NOT NULL,
    ord          INTEGER NOT NULL,
    title        TEXT    NOT NULL,
    pdf_page     INTEGER NOT NULL,
    depth        INTEGER NOT NULL,
    PRIMARY KEY (document_id, ord)
) WITHOUT ROWID;

-- One row per PDF page, with its extracted or OCR'd text.
CREATE TABLE page (
    id            INTEGER PRIMARY KEY,
    document_id   INTEGER NOT NULL,
    pdf_page      INTEGER NOT NULL,              -- zero-based PDF page index
    label         TEXT,                          -- printed page label, when the file defines one
    width_pt      REAL    NOT NULL,
    height_pt     REAL    NOT NULL,
    text          TEXT    NOT NULL DEFAULT '',
    text_source   TEXT    NOT NULL DEFAULT 'pdf' CHECK (text_source IN ('pdf', 'ocr', 'none')),
    text_quality  REAL,                          -- 0..1; low values flag the page for OCR
    needs_ocr     INTEGER NOT NULL DEFAULT 0,
    error         TEXT,                          -- why the page has no text, when extraction or OCR failed
    fingerprint   TEXT,                          -- page-text fingerprint, for matching pages across revisions
    UNIQUE (document_id, pdf_page)
);

CREATE INDEX page_needs_ocr ON page (document_id) WHERE needs_ocr = 1;

-- Match looks up the documents that share a page with a new one.
CREATE INDEX page_fingerprint ON page (fingerprint) WHERE fingerprint IS NOT NULL;

-- Word boxes for OCR'd pages, in PDF points (origin bottom-left), so selection works on scanned pages.
CREATE TABLE ocr_word (
    page_id  INTEGER NOT NULL,
    ord      INTEGER NOT NULL,
    text     TEXT    NOT NULL,
    left_pt  REAL    NOT NULL,
    top_pt   REAL    NOT NULL,
    right_pt REAL    NOT NULL,
    bottom_pt REAL   NOT NULL,
    PRIMARY KEY (page_id, ord)
) WITHOUT ROWID;

-- Inside-documents search. External content, so snippet() always quotes page.text.
CREATE VIRTUAL TABLE page_fts USING fts5(
    text,
    content = 'page',
    content_rowid = 'id',
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TRIGGER page_ai AFTER INSERT ON page BEGIN
    INSERT INTO page_fts (rowid, text) VALUES (new.id, new.text);
END;
CREATE TRIGGER page_ad AFTER DELETE ON page BEGIN
    INSERT INTO page_fts (page_fts, rowid, text) VALUES ('delete', old.id, old.text);
END;
CREATE TRIGGER page_au AFTER UPDATE OF text ON page BEGIN
    INSERT INTO page_fts (page_fts, rowid, text) VALUES ('delete', old.id, old.text);
    INSERT INTO page_fts (rowid, text) VALUES (new.id, new.text);
END;

-- Library cards: each entry and the document it shows (its current source), projected from catalog.db. A pack shows
-- its first image. A book owned elsewhere has no document: queries join doc optionally.
CREATE TABLE entry_doc (
    entry_id     INTEGER PRIMARY KEY,
    document_id  INTEGER,                        -- null for a book owned elsewhere
    kind         TEXT    NOT NULL,               -- Bibliotaph.Core.EntryKind
    copies       INTEGER NOT NULL DEFAULT 1,     -- how many files of the book the entry has: "2 copies"
    name         TEXT,                           -- a pack's name, from its folder or ZIP; the title until one is set
    members      INTEGER NOT NULL DEFAULT 0,     -- a pack's images: "120 images"
    added_utc    TEXT                            -- when a book owned elsewhere was added; a file's card uses its document's
);

CREATE INDEX entry_doc_document ON entry_doc (document_id);

-- A pack's images in file name order, for its mosaic cover, its inspector grid and stepping through it in the viewer.
-- member_entry_id is the image's own entry, hidden while it's in the pack.
CREATE TABLE entry_member (
    entry_id         INTEGER NOT NULL,
    ord              INTEGER NOT NULL,
    document_id      INTEGER NOT NULL,
    member_entry_id  INTEGER NOT NULL,
    name             TEXT    NOT NULL,           -- the image's file name
    PRIMARY KEY (entry_id, ord)
) WITHOUT ROWID;

CREATE INDEX entry_member_document ON entry_member (document_id);
CREATE INDEX entry_member_member ON entry_member (member_entry_id);

-- The file names of the images in packs, for search (F4 plan, choice 8): "kraken" finds the pack with Kraken.png in
-- it. rowid = the image's own entry (entry_member.member_entry_id); the name is without its extension.
CREATE VIRTUAL TABLE member_fts USING fts5(
    name,
    tokenize = 'unicode61 remove_diacritics 2'
);

-- Effective metadata (Bibliotaph.Core.Metadata.EffectiveMetadata), projected from catalog.db's assertions whenever they
-- change. An entry without a row has no metadata beyond its document's file name. Labels are as cards show them.
CREATE TABLE entry_meta (
    entry_id      INTEGER PRIMARY KEY,
    title         TEXT,                          -- effective title; null shows doc.display_title
    publisher     TEXT,
    series        TEXT,
    authors       TEXT,                          -- joined with "; "
    year          INTEGER,
    system_label  TEXT,                          -- "D&D 5e"
    kind_label    TEXT,                          -- document types, "Adventure"
    level_min     INTEGER,
    level_max     INTEGER,
    level_state   TEXT    NOT NULL DEFAULT 'unknown' CHECK (level_state IN ('unknown', 'known', 'na')),
    needs_review  INTEGER NOT NULL DEFAULT 0,    -- how many Needs review cards it has
    suggested     INTEGER NOT NULL DEFAULT 0,    -- shows a value nobody has confirmed
    tags          TEXT,                          -- the user's tags, joined with "; "
    confirmed_text   TEXT,                       -- for entry_fts: labels and short labels of confirmed vocabulary values
    provisional_text TEXT                        -- and of suggested ones
);

-- One row per value of a vocabulary field (system, edition, type, setting, theme, environment), for filters, facet
-- counts and field search. value is the term key.
CREATE TABLE entry_facet (
    entry_id     INTEGER NOT NULL,
    field        TEXT    NOT NULL,
    value        TEXT    NOT NULL,
    label        TEXT    NOT NULL,
    confirmed    INTEGER NOT NULL,
    PRIMARY KEY (entry_id, field, value)
) WITHOUT ROWID;

CREATE INDEX entry_facet_value ON entry_facet (field, value);

-- Entries a model has finished reading, with the model of the latest run, projected from catalog.db's classification
-- runs.
CREATE TABLE entry_ai (
    entry_id     INTEGER PRIMARY KEY,
    model        TEXT    NOT NULL
);

-- The groups of entries the Library can be limited to (slice 3): scope is a Core ScopeKeys name ("favorite" for the
-- books marked with a heart). Projected from catalog.db, which says which entries are in each.
CREATE TABLE entry_scope (
    scope     TEXT    NOT NULL,
    entry_id  INTEGER NOT NULL,
    PRIMARY KEY (scope, entry_id)
) WITHOUT ROWID;
CREATE INDEX entry_scope_entry ON entry_scope (entry_id);

-- What each group is called, for groups that have a name the user typed: a collection's scopes ("collection:12" and
-- "collection-own:12") both carry its name, so collection:"name" finds its books. Projected from catalog.db.
CREATE TABLE scope_name (
    scope  TEXT PRIMARY KEY,
    name   TEXT NOT NULL
) WITHOUT ROWID;
CREATE INDEX scope_name_name ON scope_name (name COLLATE NOCASE);

-- Each entry's own note (slice 3f), for the notes column of entry_fts, so a search finds the books whose note has the
-- words. Projected from catalog.db; page notes aren't searched yet.
CREATE TABLE entry_note (
    entry_id  INTEGER PRIMARY KEY,
    text      TEXT NOT NULL
);

-- When each entry was last opened, for Home and the Recently opened order. Projected from catalog.db's reading_state.
CREATE TABLE entry_opened (
    entry_id    INTEGER PRIMARY KEY,
    opened_utc  TEXT    NOT NULL
);

-- Every name a term goes by, in comparison form, so system:5e finds the 5th edition. Copied from catalog.db's
-- vocabulary whenever it changes.
CREATE TABLE term_alias (
    vocabulary  TEXT NOT NULL,
    alias       TEXT NOT NULL,
    value       TEXT NOT NULL,
    PRIMARY KEY (vocabulary, alias, value)
) WITHOUT ROWID;

-- Documents search over titles and effective metadata. rowid = entry_id.
-- Confirmed and provisional values sit in separate columns so ranking can prefer confirmed ones: confirmed holds the
-- labels of confirmed vocabulary values, provisional the suggested ones plus the PDF's own information, the file
-- name's title and the folder names.
CREATE VIRTUAL TABLE entry_fts USING fts5(
    title,
    subtitle,
    publisher,
    series,
    authors,
    tags,
    notes,
    confirmed,
    provisional,
    tokenize = 'unicode61 remove_diacritics 2'
);

-- Per-document, per-stage progress. A document is searchable once its Text stage is complete.
CREATE TABLE stage_status (
    document_id    INTEGER NOT NULL,
    stage          TEXT    NOT NULL,             -- Bibliotaph.Core.Stage
    status         TEXT    NOT NULL,             -- Bibliotaph.Core.StageStatus
    stage_version  INTEGER NOT NULL,
    reason         TEXT,
    updated_utc    TEXT    NOT NULL,
    PRIMARY KEY (document_id, stage)
) WITHOUT ROWID;

-- The durable job queue. Jobs are idempotent by content hash, stage and stage version,
-- so bumping one stage's version re-queues only that stage.
CREATE TABLE job (
    id                 INTEGER PRIMARY KEY,
    document_id        INTEGER NOT NULL,
    content_hash       TEXT    NOT NULL,
    stage              TEXT    NOT NULL,
    stage_version      INTEGER NOT NULL,
    status             TEXT    NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'leased', 'done', 'failed', 'blocked')),
    priority           INTEGER NOT NULL DEFAULT 0,  -- higher runs first; opening a document bumps its jobs
    attempts           INTEGER NOT NULL DEFAULT 0,
    not_before_utc     TEXT,
    lease_owner        TEXT,
    lease_expires_utc  TEXT,
    last_error         TEXT,
    created_utc        TEXT    NOT NULL,
    UNIQUE (content_hash, stage, stage_version)
);

CREATE INDEX job_ready ON job (status, priority DESC, id) WHERE status = 'pending';
CREATE INDEX job_document ON job (document_id);

PRAGMA user_version = 12;
