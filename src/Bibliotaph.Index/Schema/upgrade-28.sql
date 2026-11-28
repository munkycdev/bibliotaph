-- index.db version 27 to 28: how long an audio file plays, which its card shows.
ALTER TABLE doc ADD COLUMN duration_ms INTEGER;

PRAGMA user_version = 28;
