# Smoke test fixtures

Synthetic files made for the app's `--smoke-test` run in CI (`--smoke-files <folder>`): a two-page PDF whose
first page is labelled "i", and a small PNG. They contain no text or images from any real book. `*.pdf` is
gitignored, so `smoke-book.pdf` was added with `git add -f`.
