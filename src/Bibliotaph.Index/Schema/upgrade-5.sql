-- index.db version 4 to 5 (foundation slice, F1): the library lists entries instead of documents, so the metadata
-- tables are keyed by entry. Pages, OCR and the job queue are kept. The catalog migration gave every existing
-- document an entry with the same id, so entry_doc and entry_fts are filled from doc here and the library shows at
-- once; the metadata projection then fills entry_meta, entry_facet and entry_ai and refreshes entry_fts.

DROP TABLE doc_meta;
DROP TABLE doc_facet;
DROP TABLE doc_ai;
DROP TABLE doc_fts;

CREATE TABLE entry_doc (
    entry_id     INTEGER PRIMARY KEY,
    document_id  INTEGER NOT NULL,
    kind         TEXT    NOT NULL
);

CREATE INDEX entry_doc_document ON entry_doc (document_id);

INSERT INTO entry_doc (entry_id, document_id, kind) SELECT document_id, document_id, 'Whole' FROM doc;

CREATE TABLE entry_meta (
    entry_id      INTEGER PRIMARY KEY,
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

CREATE TABLE entry_facet (
    entry_id     INTEGER NOT NULL,
    field        TEXT    NOT NULL,
    value        TEXT    NOT NULL,
    label        TEXT    NOT NULL,
    confirmed    INTEGER NOT NULL,
    PRIMARY KEY (entry_id, field, value)
) WITHOUT ROWID;

CREATE INDEX entry_facet_value ON entry_facet (field, value);

CREATE TABLE entry_ai (
    entry_id     INTEGER PRIMARY KEY,
    model        TEXT    NOT NULL
);

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

INSERT INTO entry_fts (rowid, title, subtitle, publisher, series, authors, tags, notes, confirmed, provisional)
SELECT document_id, display_title, coalesce(meta_subject, ''), '', '', '', '', '', '',
       coalesce(meta_title, '') || ' · ' || coalesce(meta_author, '') || ' · ' || coalesce(meta_keywords, '') || ' · ' || coalesce(folder_hint, '')
FROM doc;

PRAGMA user_version = 5;
