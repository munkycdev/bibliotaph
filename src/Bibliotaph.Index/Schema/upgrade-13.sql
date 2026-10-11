-- index.db version 12 to 13 (slice 5): adventures inside a file. An adventure's card knows its file's entry and its
-- pages, a file's card how many adventures it holds, and each adventure's first page has a cover. The projection fills
-- them from catalog.db at startup; the covers are drawn again as each file's Covers stage runs.

ALTER TABLE entry_doc ADD COLUMN parent_id INTEGER;
ALTER TABLE entry_doc ADD COLUMN first_page INTEGER;
ALTER TABLE entry_doc ADD COLUMN last_page INTEGER;
ALTER TABLE entry_doc ADD COLUMN parts INTEGER NOT NULL DEFAULT 0;

CREATE INDEX entry_doc_parent ON entry_doc (parent_id) WHERE parent_id IS NOT NULL;

-- The cover of an adventure inside a file: its first page, drawn like a document's cover.
CREATE TABLE page_cover (
    document_id  INTEGER NOT NULL,
    pdf_page     INTEGER NOT NULL,
    cover        TEXT    NOT NULL,               -- cover file name in the cache folder
    PRIMARY KEY (document_id, pdf_page)
) WITHOUT ROWID;

PRAGMA user_version = 13;
