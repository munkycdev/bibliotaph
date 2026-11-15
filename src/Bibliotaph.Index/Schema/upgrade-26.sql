-- index.db version 25 to 26: whether each companion's book was only suggested by the rules, so Organize can show
-- those links and keep them. Filled by the projection every start runs.
ALTER TABLE entry_doc ADD COLUMN source_suggested INTEGER NOT NULL DEFAULT 0;

PRAGMA user_version = 26;
