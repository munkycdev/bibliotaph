-- index.db version 17 to 18 (online libraries, O2): a book owned elsewhere can have a cover, the one its online
-- library showed, kept in the covers cache. Nothing to fill: the metadata projection at the next start writes them.

ALTER TABLE entry_doc ADD COLUMN cover TEXT;

PRAGMA user_version = 18;
