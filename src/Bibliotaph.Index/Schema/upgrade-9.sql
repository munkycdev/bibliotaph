-- index.db version 8 to 9 (foundation slice, F5a): books owned elsewhere. Their cards have no document, so
-- entry_doc.document_id becomes optional, and they keep when they were added, which a file's card takes from its
-- document. SQLite can't drop NOT NULL in place, so the table is rebuilt; the metadata projection adds the books owned
-- elsewhere at startup.

CREATE TABLE entry_doc_v9 (
    entry_id     INTEGER PRIMARY KEY,
    document_id  INTEGER,
    kind         TEXT    NOT NULL,
    copies       INTEGER NOT NULL DEFAULT 1,
    name         TEXT,
    members      INTEGER NOT NULL DEFAULT 0,
    added_utc    TEXT
);

INSERT INTO entry_doc_v9 (entry_id, document_id, kind, copies, name, members)
SELECT entry_id, document_id, kind, copies, name, members FROM entry_doc;

DROP TABLE entry_doc;
ALTER TABLE entry_doc_v9 RENAME TO entry_doc;
CREATE INDEX entry_doc_document ON entry_doc (document_id);

PRAGMA user_version = 9;
