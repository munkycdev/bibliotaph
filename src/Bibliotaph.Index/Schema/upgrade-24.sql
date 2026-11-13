-- index.db version 23 to 24: what each document's last online lookup searched for, for its details to show. Written
-- by the next lookup, so a new table starts empty.
CREATE TABLE lookup_search (
    document_id  INTEGER PRIMARY KEY,
    searched     TEXT    NOT NULL
);

PRAGMA user_version = 24;
