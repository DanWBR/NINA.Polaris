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
# Unit tests for the RAG knowledge base (BM25 index over the Markdown corpus).
#
#   python -m pytest server/test_knowledge.py   (or: python server/test_knowledge.py)

from knowledge import KnowledgeBase


def _kb():
    return KnowledgeBase()


def test_polaris_product_corpus_is_indexed():
    kb = _kb()
    assert kb.ready
    sources = {p.source for p in kb.passages}
    # The synced Polaris manual lives under polaris/ and must be picked up by
    # the recursive loader (regression: the loader used to be flat).
    polaris = [s for s in sources if s.startswith("polaris/")]
    assert len(polaris) > 20, f"expected the Polaris manual to be indexed, got {len(polaris)} docs"
    # And the generic top-level astro notes must still be there.
    assert "focusing" in sources
    # The guide's README is mirrored as polaris/index (README.md is otherwise skipped).
    assert "polaris/index" in sources


def test_navigation_query_returns_a_polaris_passage():
    kb = _kb()
    # A confused-user, "where do I…" question should surface product docs.
    hits = kb.search("where do I go to plan targets for the night", k=3)
    assert hits, "expected results for a navigation query"
    assert any(h["source"].startswith("polaris/") for h in hits), \
        f"expected a Polaris manual hit, got {[h['source'] for h in hits]}"


def test_generic_astro_query_still_works():
    kb = _kb()
    hits = kb.search("why are my stars elongated", k=3)
    assert hits


if __name__ == "__main__":
    test_polaris_product_corpus_is_indexed()
    test_navigation_query_returns_a_polaris_passage()
    test_generic_astro_query_still_works()
    print("ok")
