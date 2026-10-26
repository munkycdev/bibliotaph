-- index.db version 20 to 21: results shared by the user's other devices through the web library. Both start empty,
-- so everything shared is taken again.
-- What the user's other devices worked out about files, by content hash, as taken from the web library: kept until a
-- file with that content is in this library, so it isn't sent to the AI again. content is the result's JSON.
CREATE TABLE shared_result (
    content_hash    TEXT    NOT NULL,
    kind            TEXT    NOT NULL,              -- classification
    prompt_version  INTEGER NOT NULL,
    result_id       TEXT    NOT NULL,              -- the run's id on the device that made it
    finished_utc    TEXT    NOT NULL,
    content         TEXT    NOT NULL,
    PRIMARY KEY (content_hash, kind)
);

-- How far sharing results with the web library has got: the last result taken, and the end of the last run sent.
CREATE TABLE shared_state (
    key             TEXT    PRIMARY KEY,
    value           TEXT    NOT NULL
);

PRAGMA user_version = 21;
