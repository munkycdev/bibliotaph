-- index.db version 10 to 11 (slice 3b): the names of the groups in entry_scope, so collection:"name" can find a
-- collection's books. The projection fills it from catalog.db at startup.

-- What each group is called, for groups that have a name the user typed: a collection's scopes ("collection:12" and
-- "collection-own:12") both carry its name. Projected from catalog.db.
CREATE TABLE scope_name (
    scope  TEXT PRIMARY KEY,
    name   TEXT NOT NULL
) WITHOUT ROWID;
CREATE INDEX scope_name_name ON scope_name (name COLLATE NOCASE);

PRAGMA user_version = 11;
