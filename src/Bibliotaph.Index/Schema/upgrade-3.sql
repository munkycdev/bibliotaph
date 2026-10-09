-- index.db version 2 to 3 (slice 2): adds effective metadata, facets and term aliases, and an authors column in
-- doc_fts. Pages, OCR and the job queue are kept. doc_fts is rebuilt from doc here; the metadata projection then
-- fills doc_meta and doc_facet and refreshes doc_fts again.

CREATE TABLE doc_meta (
    document_id   INTEGER PRIMARY KEY,
    title         TEXT,
    publisher     TEXT,
    series        TEXT,
    authors       TEXT,
    year          INTEGER,
    system_label  TEXT,
    kind_label    TEXT,
    level_min     INTEGER,
    level_max     INTEGER,
    level_state   TEXT    NOT NULL DEFAULT 'unknown' CHECK (level_state IN ('unknown', 'known', 'na')),
    needs_review  INTEGER NOT NULL DEFAULT 0,
    suggested     INTEGER NOT NULL DEFAULT 0,
    tags          TEXT,
    confirmed_text   TEXT,
    provisional_text TEXT
);

CREATE TABLE doc_facet (
    document_id  INTEGER NOT NULL,
    field        TEXT    NOT NULL,
    value        TEXT    NOT NULL,
    label        TEXT    NOT NULL,
    confirmed    INTEGER NOT NULL,
    PRIMARY KEY (document_id, field, value)
) WITHOUT ROWID;

CREATE INDEX doc_facet_value ON doc_facet (field, value);

CREATE TABLE term_alias (
    vocabulary  TEXT NOT NULL,
    alias       TEXT NOT NULL,
    value       TEXT NOT NULL,
    PRIMARY KEY (vocabulary, alias, value)
) WITHOUT ROWID;

DROP TABLE doc_fts;

CREATE VIRTUAL TABLE doc_fts USING fts5(
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

INSERT INTO doc_fts (rowid, title, subtitle, publisher, series, authors, tags, notes, confirmed, provisional)
SELECT document_id, display_title, coalesce(meta_subject, ''), '', '', '', '', '', '',
       trim(coalesce(meta_title, '') || ' · ' || coalesce(meta_author, '') || ' · ' || coalesce(meta_keywords, '') || ' · ' || coalesce(folder_hint, ''))
FROM doc;

PRAGMA user_version = 3;
