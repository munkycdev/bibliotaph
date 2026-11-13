-- index.db version 24 to 25: which book each companion card (a map, handout or pack) goes with, and how many go with
-- each book. Filled by the projection every start runs.
ALTER TABLE entry_doc ADD COLUMN source_id INTEGER;
ALTER TABLE entry_doc ADD COLUMN companions INTEGER NOT NULL DEFAULT 0;
CREATE INDEX entry_doc_source ON entry_doc (source_id) WHERE source_id IS NOT NULL;

PRAGMA user_version = 25;
