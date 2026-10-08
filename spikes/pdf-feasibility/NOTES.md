# Spike notes

## To do

- [ ] **Move from the .NET 11 RC to .NET 11 GA when it ships (expected November 2026).** Change `BibTfm` in
  `Directory.Build.props` only if the moniker changes (it should stay `net11.0`), install the GA SDK, and rebuild.
  .NET 11 is a standard-term release; decide deliberately whether the real app targets it or the next LTS.

## Decisions taken for the spike

- PDFium via PDFiumCore 156.0.8076, which ships prebuilt binaries for win-x64, win-arm64 and win-x86.
- PDFium runs only inside `PdfWorker`, a separate process. The host talks to it with length-prefixed JSON over a named
  pipe. Bitmaps are rendered straight into a shared memory-mapped file, so pixels never cross the pipe.
- On Windows each worker sits in its own job object: committed memory is capped (default 1 GB), and the worker dies
  with the host. A crash or a timeout kills the worker; the next request starts a fresh one.
- No source file is ever opened for writing. The harness hashes every source before and after a run.
- Fixture password `bibliotaph-fixture` is a throwaway test value for `corpus/generated/password-open.pdf` only.

## Things the spike already showed (Linux dry run, 2026-10-08)

The harness was run in a Linux container against 5 of Dave's files plus fixtures, before handing over. These are not
the Windows numbers the gates are judged on, but they confirm the pipeline works end to end:

- Every file opened; whole-book search found the expected word on the expected page, with highlight rectangles.
- Wrong or missing password: reported as `open-password`, no crash. Truncated file: reported as `open-format`.
- A damaged cross-reference table was repaired by PDFium and opened normally.
- Self-test: deliberate crash and hang were both contained and the worker restarted.
- Copying a 2800 × 4300 bitmap out of shared memory costs about as much as rendering it. If that holds on
  Windows, the real app should hand the shared buffer to WPF directly instead of copying.

## Known limits of the prototype

- Page rotation (`/Rotate`) is ignored when placing search highlights.
- Only the PDFium candidate is wired up; the pdf.js and Windows.Data.Pdf comparisons come after these results.
- Non-ASCII file paths are untested; PDFium's `FPDF_LoadDocument` path handling on Windows needs checking.
