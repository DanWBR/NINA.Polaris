# N.I.N.A. Polaris — Canopus Assistant
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
#
# Canopus Assistant — astrophotography knowledge base (RAG retrieval).
#
# A tiny, dependency-free retrieval layer over the Markdown docs in
# ./knowledge/. Docs are split into passages by heading, indexed with a
# from-scratch BM25 ranker, and queried by the `search_knowledge` agent tool so
# the assistant can ground general astrophotography tips in curated text instead
# of free-associating. Keyword/BM25 first; embeddings can layer on later.

from __future__ import annotations

import math
import os
import re
from dataclasses import dataclass, field

_KNOWLEDGE_DIR = os.path.join(os.path.dirname(__file__), "knowledge")

_TOKEN_RE = re.compile(r"[a-z0-9]+")


def _tokenize(text: str) -> list[str]:
    return _TOKEN_RE.findall(text.lower())


@dataclass
class Passage:
    source: str          # file stem, e.g. "focusing"
    title: str           # heading text
    text: str            # body under the heading
    tokens: list[str] = field(default_factory=list)
    tf: dict[str, int] = field(default_factory=dict)
    length: int = 0


def _split_passages(source: str, md: str) -> list[Passage]:
    """Split a Markdown doc into passages at `#`/`##`/`###` headings. Text
    before the first heading is attached to a synthetic 'Overview' passage."""
    passages: list[Passage] = []
    cur_title = "Overview"
    cur_lines: list[str] = []

    def flush():
        body = "\n".join(cur_lines).strip()
        if body:
            passages.append(Passage(source=source, title=cur_title.strip(), text=body))

    for line in md.splitlines():
        m = re.match(r"^#{1,3}\s+(.*)$", line)
        if m:
            flush()
            cur_title = m.group(1)
            cur_lines = []
        else:
            cur_lines.append(line)
    flush()
    return passages


class KnowledgeBase:
    """BM25 index over the astrophotography Markdown docs."""

    K1 = 1.5
    B = 0.75

    def __init__(self, directory: str = _KNOWLEDGE_DIR) -> None:
        self.directory = directory
        self.passages: list[Passage] = []
        self.df: dict[str, int] = {}
        self.avg_len: float = 0.0
        self._load()

    def _load(self) -> None:
        if not os.path.isdir(self.directory):
            return
        # Walk recursively so a product corpus in a sub-folder (e.g.
        # ./knowledge/polaris/ — the mirrored Polaris user guide) is indexed
        # alongside the top-level generic astrophotography notes.
        for root, _dirs, files in os.walk(self.directory):
            for name in sorted(files):
                if not name.lower().endswith(".md") or name.lower() == "readme.md":
                    continue  # README documents the corpus; it isn't part of it
                path = os.path.join(root, name)
                try:
                    with open(path, "r", encoding="utf-8") as f:
                        md = f.read()
                except OSError:
                    continue
                # Source id = path relative to the knowledge dir, no extension,
                # POSIX-style (e.g. "polaris/first-night", "focusing").
                rel = os.path.relpath(os.path.splitext(path)[0], self.directory)
                source = rel.replace(os.sep, "/")
                self.passages.extend(_split_passages(source, md))

        for p in self.passages:
            # Weight the title terms (repeat them) so heading matches rank well.
            p.tokens = _tokenize(p.title) * 3 + _tokenize(p.text)
            p.length = len(p.tokens)
            tf: dict[str, int] = {}
            for t in p.tokens:
                tf[t] = tf.get(t, 0) + 1
            p.tf = tf
            for t in tf:
                self.df[t] = self.df.get(t, 0) + 1

        if self.passages:
            self.avg_len = sum(p.length for p in self.passages) / len(self.passages)

    @property
    def ready(self) -> bool:
        return bool(self.passages)

    def _idf(self, term: str) -> float:
        n = len(self.passages)
        df = self.df.get(term, 0)
        # BM25 idf with +0.5 smoothing; clamp at 0 so ubiquitous terms don't go negative.
        return max(0.0, math.log((n - df + 0.5) / (df + 0.5) + 1.0))

    def _score(self, query_terms: list[str], p: Passage) -> float:
        score = 0.0
        for t in query_terms:
            f = p.tf.get(t, 0)
            if not f:
                continue
            idf = self._idf(t)
            denom = f + self.K1 * (1 - self.B + self.B * (p.length / (self.avg_len or 1)))
            score += idf * (f * (self.K1 + 1)) / (denom or 1)
        return score

    def search(self, query: str, k: int = 4, max_chars: int = 900) -> list[dict]:
        """Return up to `k` best passages as {source, title, text, score}."""
        if not self.ready:
            return []
        terms = _tokenize(query)
        if not terms:
            return []
        scored = ((self._score(terms, p), p) for p in self.passages)
        ranked = sorted((s for s in scored if s[0] > 0), key=lambda s: s[0], reverse=True)[:k]
        out = []
        for score, p in ranked:
            text = p.text if len(p.text) <= max_chars else p.text[:max_chars].rsplit(" ", 1)[0] + "…"
            out.append({"source": p.source, "title": p.title, "text": text, "score": round(score, 3)})
        return out


# Module-level singleton loaded once at import.
KNOWLEDGE = KnowledgeBase()
