-- index.db version 16 to 17 (online libraries, O1): a vocabulary value can carry a web link, such as the book's page
-- on D&D Beyond in Also own, so a card owned elsewhere opens it. Nothing to fill: the metadata projection at the next
-- start writes the links.

ALTER TABLE entry_facet ADD COLUMN link TEXT;

PRAGMA user_version = 17;
