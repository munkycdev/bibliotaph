-- index.db version 6 to 7 (foundation slice, F4): image packs. A pack card has a name from its folder or ZIP and
-- counts its images, which entry_member lists in file name order. The metadata projection fills them at startup.

ALTER TABLE entry_doc ADD COLUMN name TEXT;
ALTER TABLE entry_doc ADD COLUMN members INTEGER NOT NULL DEFAULT 0;

CREATE TABLE entry_member (
    entry_id         INTEGER NOT NULL,
    ord              INTEGER NOT NULL,
    document_id      INTEGER NOT NULL,
    member_entry_id  INTEGER NOT NULL,
    name             TEXT    NOT NULL,
    PRIMARY KEY (entry_id, ord)
) WITHOUT ROWID;

CREATE INDEX entry_member_document ON entry_member (document_id);

PRAGMA user_version = 7;
