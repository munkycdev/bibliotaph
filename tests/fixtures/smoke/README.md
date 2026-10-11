# Smoke test fixtures

Synthetic files made for the app's `--smoke-test` run in CI (`--smoke-files <folder>`): a two-page PDF whose
first page is labelled "i", with two bookmarks, and a small PNG. They contain no text or images from any real book. `*.pdf` is
gitignored, so `smoke-book.pdf` was added with `git add -f`.

`smoke-sync.json` is a made-up second PC's library sync file, holding one invented collection. `build/verify.ps1` puts it
in the folder the smoke test syncs with, as `SMOKE-PC-2.json`, so Sync with another PC has another PC's work to merge.
