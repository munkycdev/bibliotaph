-- index.db version 3 to 4 (slice 2c): adds the documents a model has read. The metadata projection fills it from
-- catalog.db's classification runs at startup.

CREATE TABLE doc_ai (
    document_id  INTEGER PRIMARY KEY,
    model        TEXT    NOT NULL
);

PRAGMA user_version = 4;
