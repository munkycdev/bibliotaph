-- index.db: everything here is derived from source files and can be thrown away.
-- There are no migrations. Change this script, bump IndexSchema.Version (and user_version below),
-- and every install deletes and rebuilds index.db on next start; documents re-queue from their hashes.
--
-- document_id values refer to document.id in catalog.db (attached at query time, so no foreign keys).

-- What the Probe stage learned about each document, and its title until slice 2's metadata takes over.
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

-- Documents search over titles and effective metadata. rowid = document_id.
-- Confirmed and provisional values sit in separate columns so ranking can prefer confirmed ones.
-- Until slice 2, title is the display title and provisional holds the PDF's own information and folder names.
CREATE VIRTUAL TABLE doc_fts USING fts5(
    title,
    subtitle,
    publisher,
    series,
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

PRAGMA user_version = 2;
