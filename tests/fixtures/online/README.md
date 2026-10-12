# Online library fixtures

Made-up library pages from online services, shaped like the real ones and saved as a browser's "Save as, Webpage,
complete" saves them: the page, and its images in a `…_files` folder beside it. Every title, publisher and image is
invented; no real page is ever committed (online libraries plan, choice 18).

`dndbeyond-library.html` is a D&D Beyond library page. Its Next.js data (`self.__next_f.push` scripts, split mid-book as
the real page splits them) lists four books: two owned, one not owned, and one shared with the user. The two owned books'
covers are in `dndbeyond-library_files`. The reader tests and the app's smoke test both read it; `build/verify.ps1` copies
it into the smoke test's files.

`demiplane-sources.html` is a Demiplane game's Sources page (a Nexus's Source Library): three source cards, two
unlocked (with the View button) and one only for sale. Their covers are saved files named as Edge names them
(`cover.jpg`, `cover(1).jpg`), as every Demiplane cover is called `cover.jpg`.

`humble-library.html` is Humble Bundle's library with eBook chosen under Platform: two products, each with its icon,
name and publisher, and no link of its own.

`drivethrurpg-library.html` is a page of DriveThruRPG's My Library: two title cards, each with its product page,
publisher and thumbnail, one product link with a query, and a link to a second page.
