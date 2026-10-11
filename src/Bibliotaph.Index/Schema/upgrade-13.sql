-- index.db version 12 to 13 (scenario review, row 6): how many players each entry is for. The projection fills it from
-- catalog.db at startup.

ALTER TABLE entry_meta ADD COLUMN players_min INTEGER;
ALTER TABLE entry_meta ADD COLUMN players_max INTEGER;

PRAGMA user_version = 13;
