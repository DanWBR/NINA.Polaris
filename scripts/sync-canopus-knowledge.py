#!/usr/bin/env python3
# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
#
# This program is free software: you can redistribute it and/or modify it
# under the terms of the GNU Affero General Public License as published by
# the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version.
#
# This program is distributed in the hope that it will be useful, but WITHOUT
# ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
# FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
# for more details. You should have received a copy of the license along with
# this program. If not, see <https://www.gnu.org/licenses/>.
"""
Build the Canopus product knowledge base from the Polaris Handbook.

Canopus grounds its answers about Polaris in the Markdown under
canopus/server/knowledge/polaris/ (a BM25 index, see canopus/server/knowledge.py).
This script turns each chapter of the handbook (book/*.qmd) into a plain
Markdown file there, so the assistant knows the product as the book describes it:

- Quarto syntax that means nothing to a text index is removed: heading
  attributes, div fences, HTML comments and image paths.
- A footnote is kept inline, in parentheses, because the book explains its
  technical terms in footnotes.
- A cross reference (@sec-...) becomes the name of the section it points to.
- Every section title is prefixed with its chapter, and a subsection title
  with its chapter and section, so a passage found on its own still says
  where it belongs ("Planetary and Lucky Imaging / The Planet + Moons
  sub-tab / Settings").

The output folder is replaced on every run. The files are committed, so the
SBC build and the cloud tier have them without a checkout of the book.

Usage:
    python scripts/sync-canopus-knowledge.py [--book <book dir>] [--dest <dir>]

The cloud tier runs the same script with --dest pointing at its own corpus.
"""

import argparse
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.dirname(_HERE)
_DEFAULT_BOOK = os.path.join(_REPO, "book")
_DEFAULT_DEST = os.path.join(_REPO, "canopus", "server", "knowledge", "polaris")

# The preface and the bibliography stub carry nothing the assistant can use.
_SKIP = {"index.qmd", "99-references.qmd"}

_CHAPTER_FILE = re.compile(r"^\d+-(.+)\.qmd$")
_HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*$")
_ATTRS = re.compile(r"\s*\{[^{}]*\}\s*$")
_ID_IN_ATTRS = re.compile(r"#((?:sec|fig|tbl)-[\w-]*\w)")
_FENCE = re.compile(r"^\s*(```|~~~)")
_XREF = re.compile(r"\[?-?@((?:sec|fig|tbl)-[\w-]*\w)\]?")
_IMAGE = re.compile(r"!\[((?:[^\[\]]|\[[^\]]*\])*)\]\([^)]*\)(\{[^}]*\})?", re.S)
_COMMENT = re.compile(r"<!--.*?-->", re.S)
_DIV = re.compile(r"^:::.*$")


def _chapters(book):
    out = []
    for name in sorted(os.listdir(book)):
        if name in _SKIP:
            continue
        m = _CHAPTER_FILE.match(name)
        if m:
            out.append((name, m.group(1)))
    return out


def _collect_ids(text):
    """Map every #sec-/#fig-/#tbl- id in a chapter to (kind, title, level)."""
    ids = {}
    in_code = False
    for line in text.splitlines():
        if _FENCE.match(line):
            in_code = not in_code
            continue
        if in_code:
            continue
        h = _HEADING.match(line)
        if h:
            attrs = re.search(r"\{([^{}]*)\}\s*$", h.group(2))
            if attrs:
                for i in _ID_IN_ATTRS.findall("{" + attrs.group(1) + "}"):
                    title = _ATTRS.sub("", h.group(2)).strip()
                    ids[i] = ("sec", title, len(h.group(1)))
            continue
        for i in _ID_IN_ATTRS.findall(line):
            ids.setdefault(i, (i.split("-", 1)[0], "", 0))
    return ids


def _inline_footnotes(text):
    """Rewrite ^[...] footnotes as ' (...)', matching nested brackets."""
    out = []
    i = 0
    while True:
        j = text.find("^[", i)
        if j < 0:
            out.append(text[i:])
            break
        out.append(text[i:j])
        depth = 0
        k = j + 1
        while k < len(text):
            if text[k] == "[":
                depth += 1
            elif text[k] == "]":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        body = " ".join(text[j + 2:k].split())
        sep = "" if (out and out[-1][-1:].isspace()) else " "
        out.append(sep + "(" + body + ")")
        i = k + 1
    return "".join(out)


def _xref(ids):
    def sub(m):
        kind, title, level = ids.get(m.group(1), (m.group(1).split("-", 1)[0], "", 0))
        if kind == "sec" and title:
            return f'the "{title}" ' + ("chapter" if level == 1 else "section")
        return {"fig": "the figure", "tbl": "the table"}.get(kind, "another section")
    return sub


def convert(text, ids):
    text = _COMMENT.sub("", text)
    text = _inline_footnotes(text)
    # A figure keeps its caption as plain text; captions span lines.
    text = _IMAGE.sub(lambda m: " ".join(m.group(1).split()), text)
    lines = []
    chapter = section = ""
    in_code = False
    for line in text.splitlines():
        if _FENCE.match(line):
            in_code = not in_code
            lines.append(line)
            continue
        if in_code:
            lines.append(line)
            continue
        if _DIV.match(line):
            continue
        h = _HEADING.match(line)
        if h:
            level = len(h.group(1))
            title = _ATTRS.sub("", h.group(2)).strip()
            title = _XREF.sub(_xref(ids), title)
            if level == 1:
                chapter, section = title, ""
            elif level == 2:
                section = title
                title = f"{chapter} / {title}" if chapter else title
            else:
                title = " / ".join(t for t in (chapter, section, title) if t)
            lines.append(f"{h.group(1)} {title}")
            continue
        line = _XREF.sub(_xref(ids), line)
        lines.append(line.rstrip())
    out = "\n".join(lines)
    out = re.sub(r"\n{3,}", "\n\n", out).strip() + "\n"
    return out


def main():
    ap = argparse.ArgumentParser(description="Build the Canopus knowledge base from the handbook")
    ap.add_argument("--book", default=_DEFAULT_BOOK)
    ap.add_argument("--dest", default=_DEFAULT_DEST)
    args = ap.parse_args()

    if not os.path.isdir(args.book):
        print(f"ERROR: handbook not found: {args.book}", file=sys.stderr)
        return 1

    chapters = _chapters(args.book)
    texts = {}
    ids = {}
    for name, _slug in chapters:
        with open(os.path.join(args.book, name), encoding="utf-8") as fh:
            texts[name] = fh.read()
        ids.update(_collect_ids(texts[name]))

    os.makedirs(args.dest, exist_ok=True)
    for name in os.listdir(args.dest):
        if name.lower().endswith(".md"):
            os.remove(os.path.join(args.dest, name))

    dashed = []
    unresolved = set()
    for name, slug in chapters:
        md = convert(texts[name], ids)
        unresolved.update(re.findall(r"@(?:sec|fig|tbl)-[\w-]+", md))
        n = md.count("–") + md.count("—")
        if n:
            dashed.append((name, n))
        with open(os.path.join(args.dest, slug + ".md"), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(md)

    print(f"Wrote {len(chapters)} chapter(s) to {os.path.relpath(args.dest, _REPO)}")
    if unresolved:
        print(f"WARNING: unresolved cross references: {sorted(unresolved)}", file=sys.stderr)
    if dashed:
        # Canopus text carries no em or en dashes; fix them in the book.
        print("WARNING: em/en dashes in " +
              ", ".join(f"{n} ({c})" for n, c in dashed), file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
