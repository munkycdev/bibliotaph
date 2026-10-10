-- index.db version 9 to 10 (slice 3a): the groups of entries the Library can be limited to, favourites first, and
-- when each entry was last opened. The projection fills both from catalog.db at startup.

-- The groups of entries the Library can be limited to (slice 3): scope is a Core ScopeKeys name ("favorite" for the
-- books marked with a heart). Projected from catalog.db, which says which entries are in each.
CREATE TABLE entry_scope (
    scope     TEXT    NOT NULL,
    entry_id  INTEGER NOT NULL,
    PRIMARY KEY (scope, entry_id)
) WITHOUT ROWID;
CREATE INDEX entry_scope_entry ON entry_scope (entry_id);

-- When each entry was last opened, for Home and the Recently opened order. Projected from catalog.db's reading_state.
CREATE TABLE entry_opened (
    entry_id    INTEGER PRIMARY KEY,
    opened_utc  TEXT    NOT NULL
);

PRAGMA user_version = 10;
