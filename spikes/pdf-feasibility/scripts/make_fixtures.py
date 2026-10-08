"""Builds the generated test PDFs in corpus/generated from copies of Dave's files.

Originals are only read. Needs Python 3 and qpdf on PATH. Usage:
    python scripts/make_fixtures.py "<D&D folder>" corpus/generated
"""
import os
import subprocess
import sys

src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)

def source(rel):
    return os.path.join(src, *rel.split("/"))

def target(name):
    return os.path.join(out, name)

small = source("Oneshots/Quartershots/QSRR-Prep-DCC-v1_0-printfriendly.pdf")

# Open password. This is a throwaway test value, not a real secret.
subprocess.run(["qpdf", "--encrypt", "bibliotaph-fixture", "bibliotaph-fixture", "256", "--",
                small, target("password-open.pdf")], check=True)

# A wrong startxref offset: readers must rebuild the cross-reference table to open it.
data = bytearray(open(small, "rb").read())
at = data.rfind(b"startxref")
data[at:] = b"startxref\n12345\n%%EOF\n"
open(target("broken-xref.pdf"), "wb").write(data)

# A download cut off at 60%.
spears = open(source("HumbleBundle/SpearsInTheIce5e.pdf"), "rb").read()
open(target("truncated.pdf"), "wb").write(spears[: len(spears) * 6 // 10])

# Three scanned pages followed by three digital pages.
subprocess.run(["qpdf", "--empty", "--pages",
                source("Game Masters Books/Games Masters Book of Astonishing Random Tables.pdf"), "20-22",
                source("Coriolis/coriolis_rulebook_ENG_digital_201022_opt.pdf"), "30-32",
                "--", target("mixed-scan-digital.pdf")], check=True)

for name in sorted(os.listdir(out)):
    print(f"{name:28} {os.path.getsize(target(name)) / 1048576:7.1f} MB")
