#!/usr/bin/env python3
"""Build the machine-readable editions of The Polaris Handbook.

Writes three files into book/_build/, published under /handbook/ by
deploy-website.yml:

  llms.txt        short index (title, summary, chapter list)
  llms-full.txt   the whole book as one plain-text file
  handbook.jsonl  a chunked RAG corpus: one JSON record per section,
                  with metadata and a deep link back to the web edition

The RAG corpus is the ingestion source for the Canopus Assistant (an
external module): each record is a retrievable unit carrying its part,
chapter, and section titles plus a stable URL into the HTML handbook so
the assistant can cite and link to the exact section. Quarto-only markup
(anchors, callouts, citation and cross-reference markers, HTML comments)
is reduced to readable text; inline footnotes become parentheticals and
figures become "Figure:" lines.

Source-based (no render needed), so it stays fast and dependency-free
(standard library only).
"""

from __future__ import annotations
import json
import re
from pathlib import Path

BOOK = Path(__file__).resolve().parent.parent
OUT = BOOK / "_build"
SITE = "https://polaris-astro.app.br/handbook"

# ---- structure, parsed from _quarto.yml so it never drifts ----

def manifest() -> list[tuple[str, str]]:
    """Ordered list of ('part', title) and ('file', name.qmd)."""
    items: list[tuple[str, str]] = []
    inbook = False
    for line in (BOOK / "_quarto.yml").read_text(encoding="utf-8").splitlines():
        if line.startswith("book:"):
            inbook = True
            continue
        if re.match(r"^[a-z]", line):  # next top-level key ends the book block
            inbook = False
        if not inbook:
            continue
        m = re.match(r'\s*- part:\s*"?(.*?)"?\s*$', line)
        if m:
            items.append(("part", m.group(1)))
            continue
        if re.match(r"^  appendices:", line):
            items.append(("part", "Appendices"))
            continue
        m = re.match(r"\s*-\s*(\S+\.qmd)\s*$", line)
        if m:
            items.append(("file", m.group(1)))
    return items

# ---- markup cleanup, shared by every output ----

def clean(text: str) -> str:
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S)          # HTML comments
    text = re.sub(r"\^\[([^\]]*)\]", r" (\1)", text)             # footnotes
    text = re.sub(r"!\[([^\]]*)\]\([^)]*\)(\{[^}]*\})?",         # images
                  r"Figure: \1", text)
    text = re.sub(r"\s*\((?:see\s+)?@(?:sec|fig|tbl)-[a-z0-9-]+\)", "", text)
    text = re.sub(r"@(?:sec|fig|tbl)-[a-z0-9-]+", "another section", text)
    text = re.sub(r"\s*\[@[^\]]+\]", "", text)                   # citations
    text = re.sub(r"\{#[^}]*\}", "", text)                       # #anchors
    text = re.sub(r"\{\.[^}]*\}", "", text)                      # .classes
    text = "\n".join(l for l in text.splitlines() if not l.startswith(":::"))
    text = re.sub(r"\n{3,}", "\n\n", text)                       # collapse blanks
    return text.strip()

def heading_title(line: str) -> str:
    return re.sub(r"\s*\{[^}]*\}\s*$", "", re.sub(r"^#+\s*", "", line)).strip()

def heading_id(line: str) -> str | None:
    """Explicit {#id} on a heading, which Quarto uses verbatim as the anchor."""
    m = re.search(r"\{#([A-Za-z][\w:-]*)", line)
    return m.group(1) if m else None

def slug(title: str) -> str:
    """Pandoc auto-identifier, as Quarto assigns to HTML headings."""
    s = title.strip().lower()
    s = re.sub(r"[^\w .-]", "", s)      # keep letters, digits, _, space, -, .
    s = re.sub(r"^[^a-z]+", "", s)      # strip up to the first letter
    s = re.sub(r"\s+", "-", s)
    return re.sub(r"-+", "-", s).strip("-")

# ---- walk the book once, collecting chapters and their sections ----

class Chapter:
    def __init__(self, part: str, stem: str, title: str):
        self.part, self.stem, self.title = part, stem, title
        # (section title or "", explicit id or None, body)
        self.sections: list[tuple[str, str | None, str]] = []

def load() -> list[Chapter]:
    chapters: list[Chapter] = []
    part = "Front Matter"
    for kind, value in manifest():
        if kind == "part":
            part = value
            continue
        if value == "99-references.qmd":       # empty #refs placeholder
            continue
        src = (BOOK / value).read_text(encoding="utf-8")
        lines = src.splitlines()
        title = heading_title(lines[0]) if lines else value
        ch = Chapter(part, value[:-4], title)
        cur_title, cur_id, buf = "", None, []
        div = 0  # fenced-div (callout) depth: a `## ` inside one is a
                 # callout title, not a document section with its own anchor
        for line in lines[1:]:
            if re.match(r"^:::+\s*\{", line):
                div += 1
            elif re.match(r"^:::+\s*$", line):
                div = max(0, div - 1)
            if div == 0 and line.startswith("## "):
                ch.sections.append((cur_title, cur_id, "\n".join(buf)))
                cur_title, cur_id, buf = heading_title(line), heading_id(line), []
            else:
                buf.append(line)
        ch.sections.append((cur_title, cur_id, "\n".join(buf)))
        chapters.append(ch)
    return chapters

# ---- outputs ----

def write_llms_full(chapters: list[Chapter]) -> None:
    out = [
        "# The Polaris Handbook", "",
        "Browser-controlled astrophotography with N.I.N.A. Polaris.",
        "Complete manual, plain-text edition for machine reading.",
        "Source: https://github.com/DanWBR/NINA.Polaris (book/).",
        f"Web edition: {SITE}/  RAG corpus: {SITE}/handbook.jsonl", "",
    ]
    part = None
    for ch in chapters:
        if ch.part != part:
            part = ch.part
            out += ["", "", f"# Part: {part}", ""]
        body = "\n\n".join(
            (f"## {t}\n\n{clean(b)}" if t else clean(b)) for t, _id, b in ch.sections
        )
        out += ["", f"# {ch.title}", "", body, ""]
    (OUT / "llms-full.txt").write_text("\n".join(out).rstrip() + "\n", encoding="utf-8")

def write_llms_index(chapters: list[Chapter]) -> None:
    out = [
        "# The Polaris Handbook", "",
        "> The single reference for N.I.N.A. Polaris, a browser-controlled",
        "> astrophotography system: installation, equipment, capture,",
        "> processing, remote access, and internals.", "",
        "- Full text: llms-full.txt",
        "- RAG corpus (one JSON record per section): handbook.jsonl",
        f"- Web edition: {SITE}/", "",
        "## Chapters", "",
    ]
    part = None
    for ch in chapters:
        if ch.part != part:
            part = ch.part
            out += ["", f"### {part}"]
        out.append(f"- {ch.title}")
    (OUT / "llms.txt").write_text("\n".join(out).rstrip() + "\n", encoding="utf-8")

def write_jsonl(chapters: list[Chapter]) -> int:
    records = []
    for ch in chapters:
        page = f"{SITE}/{ch.stem}.html"
        for sec_title, sec_id, body in ch.sections:
            text = clean(body)
            if len(text) < 40:            # skip empty / trivial sections
                continue
            if sec_title:
                anchor = sec_id or slug(sec_title)
                url = f"{page}#{anchor}"
                rid = f"{ch.stem}#{anchor}"
                heading = f"{ch.title}: {sec_title}"
            else:                          # chapter intro (before first ##)
                url, rid, heading = page, ch.stem, ch.title
            records.append({
                "id": rid,
                "part": ch.part,
                "chapter": ch.title,
                "section": sec_title or None,
                "title": heading,
                "url": url,
                "text": text,
            })
    with (OUT / "handbook.jsonl").open("w", encoding="utf-8") as f:
        for r in records:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    return len(records)

def main() -> None:
    OUT.mkdir(exist_ok=True)
    chapters = load()
    write_llms_full(chapters)
    write_llms_index(chapters)
    n = write_jsonl(chapters)
    print(f"Wrote llms.txt, llms-full.txt, and handbook.jsonl ({n} chunks).")

if __name__ == "__main__":
    main()
