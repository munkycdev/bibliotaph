-- index.db version 18 to 19: the chapters of books the AI read whole, and what it found in each. Nothing to fill: the
-- metadata projection at the next start writes them from catalog.db.

-- The chapters of a book the AI read whole, projected from catalog.db's chapter scans of the document's content: its
-- top-level bookmarks, the entries of its contents page, or runs of pages, with how far the reading has got. Pages are
-- zero-based PDF pages, inclusive. reading is a Bibliotaph.Core.ChapterReading name, source a ChapterSource name.
CREATE TABLE chapter (
    document_id  INTEGER NOT NULL,
    ord          INTEGER NOT NULL,
    title        TEXT    NOT NULL,
    first_page   INTEGER NOT NULL,
    last_page    INTEGER NOT NULL,
    source       TEXT    NOT NULL,
    reading      TEXT    NOT NULL,
    PRIMARY KEY (document_id, ord)
) WITHOUT ROWID;

-- What the AI found in each chapter: a setting, environment or theme (a vocabulary term's key) or a creature (its name
-- in lower case), on the page its quote is on. Each also reaches the book's facets as "inside:<field>", so a field
-- search finds a book through its chapters without mistaking them for the book's own values.
CREATE TABLE chapter_tag (
    document_id  INTEGER NOT NULL,
    ord          INTEGER NOT NULL,
    field        TEXT    NOT NULL,
    value        TEXT    NOT NULL,
    label        TEXT    NOT NULL,
    pdf_page     INTEGER NOT NULL,
    quote        TEXT    NOT NULL,
    PRIMARY KEY (document_id, ord, field, value)
) WITHOUT ROWID;

CREATE INDEX chapter_tag_value ON chapter_tag (field, value);

PRAGMA user_version = 19;
