-- index.db version 7 to 8 (foundation slice, F4b): the file names of the images in packs become searchable. The
-- metadata projection fills member_fts at startup.

CREATE INDEX entry_member_member ON entry_member (member_entry_id);

-- The file names of the images in packs, for search (F4 plan, choice 8): "kraken" finds the pack with Kraken.png in
-- it. rowid = the image's own entry (entry_member.member_entry_id); the name is without its extension.
CREATE VIRTUAL TABLE member_fts USING fts5(
    name,
    tokenize = 'unicode61 remove_diacritics 2'
);

PRAGMA user_version = 8;
