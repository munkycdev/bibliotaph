-- index.db version 5 to 6 (foundation slice, F2): copies. Library cards say how many files of a book they have, and
-- Match looks pages up by fingerprint. The metadata projection fills the counts at startup.

ALTER TABLE entry_doc ADD COLUMN copies INTEGER NOT NULL DEFAULT 1;

CREATE INDEX page_fingerprint ON page (fingerprint) WHERE fingerprint IS NOT NULL;

PRAGMA user_version = 6;
