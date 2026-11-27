-- index.db version 26 to 27: whether the user asked for a job again, so a book they asked to be looked up afresh is
-- looked up online rather than given what another of their devices found.
ALTER TABLE job ADD COLUMN afresh INTEGER NOT NULL DEFAULT 0;

PRAGMA user_version = 27;
