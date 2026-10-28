-- index.db version 21 to 22: the ISBNs and product codes books print about themselves. The table starts empty and the
-- Identify stage fills it from stored text, so no file is read again; an e-book read before keeps no package ISBN.
ALTER TABLE doc ADD COLUMN meta_isbn TEXT;

CREATE TABLE doc_identifier (
    document_id  INTEGER NOT NULL,
    kind         TEXT    NOT NULL,                 -- Bibliotaph.Core.IdentifierKind
    value        TEXT    NOT NULL,                 -- an ISBN's 13 digits; a product code in capitals, no spaces
    pdf_page     INTEGER,                          -- where it is first printed; null from an e-book's package
    printed      TEXT    NOT NULL,                 -- as the book writes it
    PRIMARY KEY (document_id, kind, value)
) WITHOUT ROWID;
CREATE INDEX doc_identifier_value ON doc_identifier (kind, value);

PRAGMA user_version = 22;
