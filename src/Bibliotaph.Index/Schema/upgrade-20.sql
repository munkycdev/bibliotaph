-- index.db version 19 to 20: the file a user chose as a pack's cover. Nothing to fill: the projection at the next
-- start writes it from catalog.db.
ALTER TABLE entry_doc ADD COLUMN cover_member INTEGER;

PRAGMA user_version = 20;
