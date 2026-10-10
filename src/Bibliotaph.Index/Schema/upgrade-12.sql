-- index.db version 11 to 12 (slice 3f): each entry's own note, which entry_fts's notes column searches. The projection
-- fills it from catalog.db at startup, refreshing the search rows of the entries that have one.

-- Each entry's own note. Projected from catalog.db.
CREATE TABLE entry_note (
    entry_id  INTEGER PRIMARY KEY,
    text      TEXT NOT NULL
);

PRAGMA user_version = 12;
