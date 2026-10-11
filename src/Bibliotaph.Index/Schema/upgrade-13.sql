-- index.db version 12 to 13 (Ask this book, Q1): a vector for each page, made the first time its book is asked, so
-- its pages can be found by meaning. Nothing to fill: books get theirs when they are next asked.

-- One vector per page, by the embedding model and text version in model and version.
CREATE TABLE page_vec (
    page_id  INTEGER PRIMARY KEY,
    model    TEXT    NOT NULL,
    version  INTEGER NOT NULL,
    vector   BLOB    NOT NULL
);
CREATE TRIGGER page_vec_ad AFTER DELETE ON page BEGIN
    DELETE FROM page_vec WHERE page_id = old.id;
END;
CREATE TRIGGER page_vec_au AFTER UPDATE OF text ON page WHEN new.text IS NOT old.text BEGIN
    DELETE FROM page_vec WHERE page_id = old.id;
END;

PRAGMA user_version = 13;
