# Kindle test books

`sample.epub` is a made-up three-chapter book (a cover, a picture, a stylesheet, a nested contents entry, links
between chapters, a web link and a few non-ASCII characters). The Kindle files are Calibre's conversions of it:

```powershell
ebook-convert sample.epub sample-old.mobi --mobi-file-type old    # Mobipocket 6 only
ebook-convert sample.epub sample-both.mobi --mobi-file-type both  # Mobipocket 6 and KF8 in one file
ebook-convert sample.epub sample.azw3                             # KF8 only
```

They were made with Calibre 7.6. Their text and pictures are the sample's own, so they can be committed.
