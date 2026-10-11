# Online library fixtures

Made-up library pages from online services, shaped like the real ones and saved as a browser's "Save as, Webpage,
complete" saves them: the page, and its images in a `…_files` folder beside it. Every title, publisher and image is
invented; no real page is ever committed (online libraries plan, choice 18).

`dndbeyond-library.html` is a D&D Beyond library page. Its Next.js data (`self.__next_f.push` scripts, split mid-book as
the real page splits them) lists four books: two owned, one not owned, and one shared with the user. The two owned books'
covers are in `dndbeyond-library_files`. The reader tests and the app's smoke test both read it; `build/verify.ps1` copies
it into the smoke test's files.
