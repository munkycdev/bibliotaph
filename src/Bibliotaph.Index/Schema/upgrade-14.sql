-- index.db version 13 to 14 (scenario review, row 7): the software each PDF says made it, whose name can say which
-- store sold it. Files read before this version have none until they are read again (Reprocess); their store still
-- comes from folder and file names and their watermark.

ALTER TABLE doc ADD COLUMN meta_producer TEXT;

PRAGMA user_version = 14;
