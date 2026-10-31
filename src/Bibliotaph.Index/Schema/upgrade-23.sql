-- index.db version 22 to 23: each card's vector of what it is about, for search by meaning. Made in the background
-- from its title, details, notes and what the AI found in its chapters, so a new table starts empty.
CREATE TABLE entry_vec (
    entry_id      INTEGER PRIMARY KEY,
    model         TEXT    NOT NULL,
    version       INTEGER NOT NULL,
    profile_hash  TEXT    NOT NULL,                -- of the text it was made from; a card whose text has changed gets a new one
    vector        BLOB    NOT NULL
);
CREATE TRIGGER entry_vec_ad AFTER DELETE ON entry_doc BEGIN
    DELETE FROM entry_vec WHERE entry_id = old.entry_id;
END;

PRAGMA user_version = 23;
