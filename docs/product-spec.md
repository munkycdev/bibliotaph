# Bibliotaph — Product and UX Specification

Version: 0.3 · 8 October 2026 · Status: product name, Section 14 defaults and the architecture accepted; detailed requirements remain a working specification. Architecture: [architecture.md](architecture.md)

Product name: Bibliotaph. Selected by Dave on 7 October 2026. Visual direction follows [bibliotaph-ui-mockups.html](bibliotaph-ui-mockups.html). Implementation stack decided 8 October 2026 (see Section 13).

## 1. Product intent

Help a game master discover and use the RPG material they already own, without manually cataloging every file or reorganizing their existing folders.

The defining question is: **“What do I own that would help me prepare or run this session?”**

The application is a Windows-first desktop library that indexes existing files in place, automatically proposes useful RPG metadata, and returns both books and relevant pages. It runs when opened, requires no always-on server or Docker installation, and remains useful without an AI connection.

This specification consolidates the preceding discussion into proposed requirements. “Must” indicates required behavior for the assigned release, not a claim that the feature is already implemented. Performance and quality figures below are proposed acceptance targets to validate with Dave’s collection.

### Product principles

1. **Find useful content quickly.** Discovery, browsing, and opening the right page are the primary activities.
2. **Keep the files where they are.** Original files and folder structures remain untouched.
3. **Automate routine cataloging.** Review exceptions rather than approve every document.
4. **Show the evidence.** Inferences, confirmed values, and unknowns must be distinguishable.
5. **Preserve user work.** Reindexing must not erase corrections, notes, collections, or session packs.
6. **Make partial success useful.** A book can be searchable while OCR or AI processing continues.
7. **Keep operation simple.** Closing the app stops its processing; reopening resumes it.

### Primary user and jobs

The initial user is a game master with a large, mixed collection of purchased books, bundles, adventures, supplements, maps, and handouts across multiple RPG systems and editions.

| Job | Desired outcome |
|---|---|
| Prepare a game | Find a suitable adventure, maps, encounters, and rules |
| Remember something half-forgotten | Locate a passage using a phrase, topic, or rough description |
| Explore owned material | Browse attractive covers and discover related resources |
| Add a bundle | Point at its folder and let the app perform routine cataloging |
| Run a session | Open prepared books and page references with minimal navigation |
| Maintain the library | Correct occasional mistakes without becoming a full-time librarian |

## 2. Scope and release boundaries

### Release A — usable personal library

This is the first end-to-end release. It must include automatic classification; a PDF grid with manual tags alone does not satisfy the product goal.

| Area | Release A requirement |
|---|---|
| Sources | Multiple local folders; recursive PDF, JPG, and PNG discovery; exclusions |
| Processing | Hashing, text extraction, covers, page-aware indexing, selective OCR |
| Cataloging | Folder/filename hints; editable structured metadata; AI-assisted classification |
| Discovery | Cover grid, detail list, filters, full-text search, page results |
| Reading | Embedded PDF viewer, page navigation, text search, external opening |
| Organization | Favorites, manual collections, saved searches |
| Preparation | Basic ordered session packs containing files and page ranges |
| Protection | Password handling, permission-aware capabilities, metadata-only records |
| Maintenance | Durable work queue, pause/resume, review exceptions, relink missing files |
| Portability | Catalog backup/restore and metadata export; originals excluded by default |

Release A supports an AI-disabled mode and one fully implemented AI provider adapter. Provider choice is a technical discovery decision. The architecture must permit local and cloud adapters; shipping every provider is not a release gate. A local provider may require an optional separately installed model runtime, but that runtime must not be needed for the core library.

### Release B — richer discovery

Semantic retrieval, editable natural-language query interpretation, related resources, compact table mode, richer map filters, and optional vision analysis of image-only maps. Hybrid search combines lexical and semantic results while preserving explicit filters.

### Release C — grounded assistance and broader formats

Library Q&A with page citations; optional detected sections such as encounters or tables; EPUB support; richer session notes and export. These features require separate acceptance work and must not delay Release A.

### Explicitly outside the initial product

Cloud sync, multi-user accounts, player portals, VTT integration, campaign authoring, character sheets, ebook conversion, source-file renaming or deletion, DRM removal, a plugin marketplace, and running a background server. No built-in storefront or recommendation engine for buying more books.

## 3. Information architecture

Permanent navigation: **Home · Library · Collections · Sessions · Needs Review**. User-created saved searches appear under **Smart Views**. Settings and processing status remain available at the bottom of the navigation rail.

The search box is persistently available. Its default scope is the entire library; a visible scope control can restrict it to a collection or session. Entering a search from a collection must not silently limit the search without displaying that scope.

### Core concepts

| Concept | Meaning |
|---|---|
| Source folder | A watched root containing original files |
| Document | A catalog entry representing one specific content version |
| File location | A path at which that document exists; identical copies can share a document |
| Page | An addressable page with extracted text and optional printed page label |
| Collection | A manually curated, non-exclusive group of documents |
| Smart View | A saved query and filters that update as the catalog changes |
| Session pack | An ordered working set of document and page-range references |
| Metadata assertion | A value with origin, evidence, and review state |
| Capability | An operation currently available for a particular document |

“Books,” “Adventures,” and “Maps” are shortcuts or filters, not mutually exclusive storage containers. A sourcebook with maps stays a sourcebook and can also appear in searches for map-containing documents.

## 4. Main user journeys

### 4.1 First run: make existing folders useful

1. Explain that the app builds a local catalog and leaves source files in place.
2. Choose one or more folders using the native folder picker.
3. Preview discovered counts by supported file format, excluded folders, and inaccessible paths.
4. Offer folder-label inference with examples. Store folder names as provenance; do not turn every path segment into an authoritative tag.
5. Select AI mode: disabled, supported local provider, or supported cloud provider. Explain what data the selected provider receives. Cloud analysis requires explicit opt-in.
6. Start indexing and open Library immediately. Newly discovered items appear incrementally.
7. Display separate progress for discovery, text/OCR, and classification; show usable documents before the complete queue finishes.

Scanning must not require the user to choose a taxonomy before seeing their books. Supply a modest editable vocabulary and improve it through corrections.

### 4.2 Find an adventure

Release A: select Adventures, filter by system and level, add a topic or phrase, inspect matches, open a relevant page, and add the result to a session.

Release B: enter “Something creepy for four level-3 characters that fits in three hours.” Show an editable interpretation: adventure, level 3, horror-like tone, target duration 180 minutes, party size 4. A value is a strict constraint only when the user applies or confirms it. Missing duration or party-size data must be labeled unknown rather than invented. If only approximate matches exist, say which constraints are unmet.

### 4.3 Find something inside a book

Search “chase through a city.” Return grouped page hits with book title, page label, PDF page position, snippet, and match explanation. Selecting a hit opens the embedded viewer at that page with highlights where extraction coordinates allow. The user can pin the page or a page range into a session pack.

### 4.4 Add a purchased bundle

The next scan discovers new files. Existing content hashes avoid redundant processing. Classification populates supported metadata, while conflicts and unknowns enter Needs Review. The summary reads, for example, “84 discovered · 70 searchable · 9 processing · 5 need attention,” using actual counts rather than a single ambiguous completion percentage.

### 4.5 Prepare and run a session

Create a named pack, add whole documents or specific page ranges, label items, optionally group them into Adventure, Maps, Encounters, and Rules, then reorder them. Run mode presents the ordered items and a large viewer with next/previous item controls. This is a personal preparation surface, not a player-facing presentation mode.

## 5. Screen and interaction specification

### 5.1 Home

Purpose: resume work and rediscover useful material.

Content priority: resume last session pack; recently opened; recently added; favorite collections. Release B adds related-resource suggestions with a clear reason such as “Related to your Gribbits collection.” Avoid a compulsory activity dashboard. Users can set Library as their startup screen.

### 5.2 Library browser

Desktop layout: a narrow navigation rail, the main browsing area, and an optional details inspector. Filters open in a collapsible panel within the browsing area rather than permanently adding a fourth dense column.

The toolbar contains scope, search, active filter chips, result count, sort, and view toggle. Sort options: relevance while searching, title, recently added, recently opened, publisher. Display preferences persist per view.

**Cover grid:** document cover or image thumbnail, title, system/edition, type, favorite indicator, and exceptional status only. Preserve aspect ratios and letterbox unusual maps. Hover actions also appear on keyboard focus. Failed covers use an attractive title placeholder.

**Detail list:** small cover, title, publisher, system, document type, level range, and a short snippet or metadata summary. Search uses this mode by default; browsing uses the cover grid. Changing view preserves query, selection, and scroll position where practical.

Single selection shows the inspector. Double-click or the explicit Open action opens the document; keyboard Enter does the same. Right-click and an accessible overflow menu expose Add to collection, Add to session, Favorite, Edit metadata, Open externally, and Show in folder.

Bulk selection provides additive/removal tag operations, collection membership, and metadata changes with a preview of affected fields. Mixed values are shown as mixed, never as blank values that accidentally overwrite existing data. Reversible catalog actions offer Undo.

### 5.3 Search and filters

Release A supports ordinary words, quoted phrases, and documented field syntax such as `type:adventure level:3`. Unsupported syntax produces a readable error and preserves the query. Fields autocomplete; special characters can be quoted. Suggested initial fields: title, publisher, system, edition, type, level, theme, tag, and format.

Facet rules:

- Different dimensions combine with AND; multiple choices within one dimension default to OR.
- A level filter matches a document when the requested level lies within its stated minimum/maximum, for the selected system. Level has no assumed meaning across unrelated systems.
- Unknown is a first-class filter value. Unknown level or duration does not satisfy a strict numeric filter; an explicit “Include unknown” switch can broaden it.
- D&D editions and compatibility claims remain distinct. Do not silently treat 2014 and 2024 rules compatibility as equivalent.
- Counts reflect the current query and other dimensions; the selected facet’s counts describe potential matches within that dimension.
- Save View captures query, filters, scope, sort, and layout, not a frozen result list.

Results have two principal tabs: **Documents** and **Inside documents**, with counts. “Maps” is a filter rather than a third overlapping result universe. Release A can find standalone images by metadata and PDF pages by extracted text; it does not promise visual understanding of unlabeled map artwork.

Document ranking initially favors exact titles, then confirmed metadata, provisional metadata, and body text. Page results group hits by document with Expand matches. Snippets must come from the indexed page, not AI-written paraphrases presented as quotations. Explicit filters are never silently relaxed.

Release B adds semantic ranking and explains matches as “Text match,” “Metadata match,” or “Related meaning.” Do not display uncalibrated similarity scores as confidence percentages.

Zero results show active constraints, a clear-all action, and optional broaden-search suggestions. Incomplete indexing displays coverage: for example, “Searching text in 680 of 742 documents; remaining files are processing or unavailable.”

### 5.4 Document viewer and inspector

The reading workspace provides a back action that restores prior search state, document title, favorite, Add to session, and Open externally.

The viewer has page jump, zoom, fit width/page, thumbnails or outline, search within the book, and text selection with copy where the PDF’s permissions allow it (scanned pages use their OCR text). Opening from search lands on the hit. Display both labels when they differ: “Printed p. 42 · PDF page 46 of 180.” Persist last reading position independently from temporary search navigation.

The right inspector has **Details**, **Notes**, and eventually **Related** tabs. Details include editable metadata, tags, source paths, capabilities, and classification evidence. Source paths remain secondary information.

Each inferred field has a subtle Suggested indicator. Expanding it shows origin, evidence page(s), and whether the value was inferred from limited sampling. Human-confirmed values are locked against automatic replacement. Never place a decorative “AI confidence 94%” on the entire book.

Release A notes are catalog notes and page-linked notes; PDF annotation editing is deferred. For JPG/PNG, use an image viewer with zoom and pan; no fictitious page count is required.

### 5.5 Needs Review

Separate **Metadata suggestions** from **Files needing attention**. Reasons include conflicting system/edition, ambiguous title, new vocabulary, password required, unreadable file, missing location, and failed processing.

For metadata, show current value, proposed value, evidence, and Accept/Edit/Reject. Permit field-level decisions and bulk actions over genuinely comparable cases. Rejection persists against repeat processing of the same proposal; a new model version must not quietly erase it.

High-evidence, low-risk metadata can be applied provisionally under the user’s automation policy. Default automation requires supporting evidence and no conflict. Unsupported guesses remain unknown. Ambiguous fields enter review while the rest of the document remains usable. The user can choose “Review all suggestions” without changing the catalog model.

Review is never a blocking prerequisite for browsing. Dismissing an issue hides its notification, not the document or failure state.

### 5.6 Collections and Sessions

Collections can be nested; membership is non-exclusive. Removing a collection removes only its grouping. Smart Views are edited through their saved query; users cannot manually add a document to make it satisfy the query.

Session packs support title, optional date, notes, ordering, sections, and references to a document or page range. A reference can have a custom label such as “Warehouse ambush.” A document can appear several times at different pages. Copying a pack copies references and notes, not source files.

Missing resources remain visible with Relink. If a source version changes, page references require revalidation; never silently jump to the same numeric page in unrelated revised content.

### 5.7 Settings and processing

Settings areas: Sources, AI and privacy, Vocabulary, Processing, Appearance/accessibility, Backup/export. Source settings expose last scan, availability, counts, exclusions, and Rescan. Remove source explains whether catalog entries are retained or forgotten; source files are always preserved.

Processing status provides pause/resume, current stage, queued count, failures, retry, and resource preference. Start with conservative resource use. OCR and AI can be paused independently of discovery and browsing. When the app closes, active work checkpoints safely; optional tray operation is a later enhancement, not the default.

## 6. Metadata and classification requirements

### Metadata dimensions

| Dimension | Examples / semantics |
|---|---|
| Identity | Title, subtitle, authors, publisher, series, publication year |
| Format | PDF, JPG, PNG; determined from actual file type |
| Game system | Dungeons & Dragons, Pathfinder, Call of Cthulhu, system-agnostic, unknown |
| Edition / compatibility | Explicit edition and separately sourced compatibility claims |
| Document types | Adventure, rulebook, bestiary, setting, supplement, map pack, handout; multiple allowed |
| Levels | Optional system-scoped min/max; unknown and not applicable are distinct |
| Intended party | Optional stated party size and constraints |
| Duration | Stated or estimated minutes/sessions, with origin; never assume all one-shots last three hours |
| Setting | Named settings; multiple or unknown |
| Environments | Urban, forest, coastal, underground, arctic |
| Themes and tone | Horror, mystery, political intrigue, comedic |
| Content features | Maps, monsters, NPCs, tables, player options, spells, items |
| Map properties | Grid/gridless, scale, dimensions, location type; unknown unless evidenced |
| User tags | Flexible labels that supplement structured dimensions |
| Description | Short sourced summary; generated summaries labeled accordingly |
| File facts | Size, page count, content hash, paths, timestamps, language, capabilities |

Do not infer absence from incomplete analysis. If the sampled pages contain no maps, “contains maps” remains unknown rather than false. A creature appearing once in a 400-page rulebook should not automatically become a prominent book-level theme.

### Vocabulary behavior

Provide editable starter vocabularies and aliases. Normalize spelling and exact equivalents, but preserve useful distinctions: a haunted mansion can be a narrower concept under haunted locations rather than being flattened into it. New model-proposed terms enter a vocabulary review queue or map to existing terms when the equivalence is clear. Merging terms previews affected documents and is reversible.

### Classification process

1. Gather embedded metadata, filename and folder clues.
2. Extract text with page boundaries; assess whether OCR is needed per page.
3. Identify informative sections such as title page, contents, introduction, and headings.
4. Send bounded text and explicit field definitions to the selected classifier.
5. Require structured output, evidence references, and an unknown option.
6. Validate types, ranges, vocabulary, contradictory claims, and referenced pages.
7. Apply eligible provisional assertions; queue exceptions; preserve user overrides.

Store provider/model, prompt/schema version, input content version, analyzed page coverage, and processing timestamp. Reclassification is explicit or triggered by changed content/settings, not on every application launch. Cache completed work. Local model availability and cloud rate limits must not block ordinary searching.

Treat document text as untrusted content: instructions inside a PDF cannot change system prompts, activate tools, transmit other files, or change provider settings. Classification is a bounded data transformation.

## 7. File lifecycle and indexing

Use file events while running and reconciliation scans on startup/manual request; neither event streams nor timestamps alone are authoritative. Wait for files being copied to stabilize before processing. Avoid symbolic-link cycles and overlapping-root duplicate discovery.

Use inexpensive file facts to detect candidates for hashing, then content hashes to identify exact duplicates. Exact duplicates share derived content and expose all locations. Similar titles, watermarked editions, or revised PDFs are not automatically merged.

When a file moves, reconcile by identity/hash and preserve annotations and memberships. If a path’s content changes, record a new content version and rebuild affected derived data. Metadata overrides persist but may be flagged for review; page anchors are marked stale where their validity is uncertain.

Distinguish an unavailable drive/root from a missing file. Never mass-mark files deleted because a removable drive is unplugged. Network shares are best-effort sources in Release A. Cloud placeholder (online-only) files are always indexed, which downloads them as the queue reaches them: local files are processed first, first run shows how much will download, and downloading pauses when free disk space runs low. Decided by Dave on 8 October 2026.

Each processing stage has independent status: pending, running, complete, partial, blocked, failed, or skipped. Jobs are durable, idempotent, cancellable, and retryable. A corrupt PDF cannot halt the batch. Retries have limits; disk-full or credential failures stop affected work and expose a clear recovery action.

OCR must preserve page mapping, avoid rewriting originals, and retain quality indicators. Multicolumn layouts and poor scans can degrade retrieval; low-quality extraction is visible. Enforce limits on page rendering, decompression, memory, and task duration to isolate pathological files.

## 8. Passwords, restrictions, and external DRM

Protection type and available capabilities are separate. Capability records include render, extract text, index, thumbnail, and open externally, plus a reason when unavailable.

| Situation | Required experience |
|---|---|
| Ordinary PDF | Normal processing subject to parser support |
| Open password required | Catalog visible; prompt on use or queue Password needed |
| Correct password supplied | Enable permitted operations; offer a “Remember this password” option (off by default) stored in the OS credential vault, keyed by content hash |
| Wrong password | Preserve record and queue; retry only on user input |
| Extraction restricted | Offer permitted viewing and metadata search; explain indexing limitation |
| External DRM | Keep metadata-only record and launch configured authorized reader where available |
| Unknown encryption / unsupported file | Explain limitation; retain catalog information and external-open option |

No password guessing, DRM removal, or permission bypass is part of the product. Reader integrations are capability-dependent; external launching does not guarantee page navigation or entitlement.

Passwords and API keys must not appear in the catalog database, logs, exports, or prompts. Use the OS credential vault when saving secrets. Unlocking for indexing creates readable derived data; explain this before indexing protected content and allow view-only use. Removing a stored password does not pretend to erase already indexed content: provide a separate Purge derived content action that removes text, previews, embeddings, and cached AI payloads for that document. Metadata and user notes can be retained by explicit choice. Backups may still contain earlier derived data and must be described accordingly.

## 9. AI, privacy, and offline behavior

The catalog, original-file access, manual editing, lexical search, and viewing work offline after necessary components are installed. AI is optional at runtime even though AI-assisted classification is a required product capability.

Before cloud processing, disclose the configured provider and whether extracted text, page images, or both will be transmitted. Default to bounded text for classification; vision requires separate selection. Support per-source exclusions and a local-only setting. Never fall back from local processing to a cloud provider automatically.

Cloud work has per-run document limits, a usage summary, and a configurable spend limit where costs can be tracked. If pricing is unavailable, show token/request limits rather than a fabricated currency estimate. Do not promise provider retention policies the app cannot control.

Release B embeddings and generation are separately configurable capabilities; changing an embedding model triggers a controlled rebuild because vectors from incompatible models cannot be mixed. Generation failure does not invalidate completed text indexing.

Release C Q&A answers only from retrieved library evidence, cites exact documents/pages, distinguishes quotations from synthesis, and says when evidence is insufficient. It cannot assume every passage in every owned book has been searched successfully.

## 10. Data ownership and recovery

Keep user-authored catalog data separate from rebuildable derived content. A full index rebuild may replace text, thumbnails, and embeddings, but must preserve collections, tags, overrides, notes, and session references.

Backup includes the catalog and user work, with optional derived data. Source PDFs/images are excluded unless a future explicit archive feature is added. Restore previews required root-path remapping and missing resources. Secrets are excluded and re-entered after restore.

Export documented JSON for full catalog portability and CSV for flat metadata. Include stable IDs, relative/source paths, hashes, provenance, collection memberships, and page-reference conventions in the structured export. Do not imply CSV alone can preserve every nested structure.

Database migrations require a pre-migration backup and a recovery path. Failed upgrades must not destroy the only copy of user-created catalog data.

## 11. Visual design and accessibility

Visual direction: a contemporary media library with readable typography, generous cover space, restrained accent color, and quiet application chrome. Avoid faux parchment, ornate fantasy lettering for controls, and excessive administrative detail. Dark and light themes use the same information hierarchy.

Prioritize title and cover, then game system/type, then situational details. Status indicators appear when they change what the user can do; the browser should not be a wall of badges.

All key flows must work by keyboard, with visible focus, labeled controls, screen-reader names, and no hover-only actions. Use text plus icons for status, sufficient contrast, scalable text, reduced-motion support, and readable layouts at Windows display scaling up to 200%. At narrower widths, collapse the inspector and filters into drawers rather than shrinking the reader to an unusable strip.

Suggested shortcuts: Ctrl+K global search, Ctrl+F search within the current book, Enter open selected result, Esc close transient panel, and normal platform multi-select conventions. Document and expose shortcuts; avoid overriding text-editing behavior.

Empty states must tell the user what is missing and provide one relevant action. Error messages state the affected operation and a recovery action; technical diagnostics are expandable.

## 12. Quality targets and acceptance scenarios

### Proposed measurable targets

Benchmark on a documented Windows laptop, local SSD, a 5,000-document catalog, and a separately recorded indexed-page count. Capture hardware, corpus composition, and warm/cold cache conditions; these are goals, not measured claims.

| Measure | Proposed gate |
|---|---|
| Warm lexical search/filter | 95th percentile within 1 second |
| First usable library view | Within 3 seconds after normal launch |
| Responsiveness during processing | Navigation remains interactive; no processing on UI thread |
| Source integrity | Zero unintended source-file writes across acceptance corpus |
| Recovery | Restart after interruption without lost user edits or duplicated memberships |
| Classification precision | At least 95% correct among automatically applied title/system/type/publisher assertions on reviewed pilot set |
| Classification usefulness | At least 70% of evaluable core fields populated without human edits on the same set; report unknowns and coverage separately |
| Exception burden | Target fewer than 20% of ordinary readable pilot documents needing core-metadata correction |
| Page fidelity | All tested hits open their indexed PDF page; printed labels verified where available |

Select a representative pilot of approximately 100 files: ordinary digital books, scans, multi-column text, obscure independent works, long compilations, password-protected PDFs, duplicates, revised editions, standalone maps, and malformed files. Report extraction failures and difficult categories explicitly; do not inflate classification quality by excluding them silently. Adjust automatic application thresholds based on observed precision, not model self-reported certainty.

### Release A acceptance scenarios

| ID | Scenario | Pass condition |
|---|---|---|
| A01 | Add overlapping source folders | One catalog document per exact content version; all relevant paths retained |
| A02 | Search before indexing finishes | Completed documents searchable; remaining coverage disclosed |
| A03 | Find a phrase on a known page | Correct snippet and exact PDF page open |
| A04 | Review an AI error and rescan | Corrected field retained; rejected proposal not reapplied |
| A05 | Process a scanned and mixed PDF | OCR only where needed; page references remain aligned |
| A06 | Rename/move a file | Identity, collections, and session references retained after reconciliation |
| A07 | Disconnect a source drive | Source shown offline; entries not deleted |
| A08 | Replace a PDF with a revised edition | Content change detected; stale page references flagged |
| A09 | Unlock a password PDF | Permitted capabilities enabled; optional saved secret outside catalog |
| A10 | Encounter unsupported DRM | Metadata usable and external-open path offered without claimed full-text support |
| A11 | Interrupt indexing or lose AI connection | Resume eligible jobs; completed work and user edits preserved |
| A12 | Filter by level with unknown metadata | Strict results exclude unknowns unless explicitly included |
| A13 | Add and reorder page ranges in a session | Each entry opens the correct range and preserves custom labels |
| A14 | Disable AI and network | Existing library, lexical search, editing, and reading remain usable |
| A15 | Restore backup on a different root path | User work restored; files remapped or clearly marked unavailable |
| A16 | Classify a PDF containing malicious instructions | Content cannot alter provider configuration or trigger unrelated actions |
| A17 | Use keyboard and high display scaling | Core workflows remain accessible without clipped primary controls |
| A18 | Process a corrupt or extremely large file | Failure isolated; queue continues and reason is recoverable |

## 13. Technical direction and implementation sequence

This is a product specification, not a final engineering design. The engineering design is [architecture.md](architecture.md), agreed on 8 October 2026: WPF on .NET 11 with PDFium in isolated worker processes, SQLite with FTS5 (a migrated catalog database for user work and a rebuildable index database), Windows OCR pending a bake-off against Tesseract, and a local-first, configurable AI adapter (OpenAI-compatible endpoint). The PDF feasibility spike is in `spikes/pdf-feasibility/`.

A .NET-oriented implementation is a reasonable candidate given the intended builder’s experience. Validate the actual PDF rendering/extraction, encrypted-document behavior, OCR packaging, local search, and installer experience before selecting the UI framework. Avoid tying product requirements to an untested dependency or assuming optional local inference is effortless on a particular GPU.

Candidate modules: desktop shell; source scanner; document processor; metadata/classification service; search service; viewer; organization/session service; durable job queue; credentials and backup service. Keep these as internal boundaries unless a concrete need justifies separate processes.

### Build slices

1. **Feasibility slice (done for PDF, 8 October 2026; OCR still to test):** representative files through extraction, rendering, OCR, password handling, and an installable shell. Validate dependency licensing before committing to distribution.
2. **Vertical discovery slice:** select folder → discover → index pages → search → open exact page. Prove identity and source-file preservation.
3. **Catalog automation slice:** structured metadata → one AI adapter → evidence validation → correction/review → persistent overrides.
4. **Preparation slice:** collections, saved views, session page references, and browsing polish.
5. **Release hardening:** move/offline/version behavior, restart recovery, backup/restore, accessibility, and pilot acceptance gates.
6. **Release B:** semantic retrieval and natural-language interpretation only after establishing a measurable lexical-search baseline.

## 14. Decisions and open questions

Dave accepted all proposed defaults on 7 October 2026 and subsequently selected Bibliotaph as the product name. These decisions are the agreed baseline. Implementation choices and validation work identified below remain open.

| Decision | Accepted baseline | When to revisit |
|---|---|---|
| Platform | Windows first; WPF on .NET 11 (decided 8 October 2026) | Move to .NET 12 LTS when it ships |
| Storage | Index files in place; no source modifications | Stable product constraint |
| Running model | Process while app is open | If unattended indexing becomes important |
| Supported media | PDF, JPG, PNG initially | EPUB and additional image formats after core delivery |
| Provider breadth | One complete adapter in Release A: local first (OpenAI-compatible endpoint), endpoint and model configurable | Add a cloud adapter only if local misses the pilot gates |
| AI automation | Evidence-backed provisional metadata; review exceptions | Calibrate using pilot corpus |
| Page search | Included in Release A | Stable product requirement |
| Semantic search | Release B | Pull forward only if lexical baseline demonstrably fails core tasks |
| Session packs | Basic page-linked packs in Release A | Validate use during an actual game |
| Protected content | Authorized use, explicit derived-data choice, metadata fallback | Validate with representative owned files |
| Folder taxonomy | Preserve provenance; propose mappings | Tune after seeing actual folder structure |
| Product name | Bibliotaph | Selected; visual branding remains open |

Before implementation, gather collection size/page volume, representative files, source locations, likely use of removable/cloud drives, and preference for local versus cloud classification. These inputs refine sizing and dependency choices; they do not change the central promise: find useful owned material without turning its owner into the tagging department.
