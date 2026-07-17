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
# Guards the REDUCED local-tier catalog (catalog.local.json): it must stay a small,
# curated subset of the full catalog (the local model can't ingest the full one on
# an SBC), text-only (no vision analyze_* tools), and self-consistent.

import json
import os

_TOOLS_DIR = os.path.join(os.path.dirname(__file__), "..", "shared", "tools")


def _load(name):
    with open(os.path.join(_TOOLS_DIR, name), "r", encoding="utf-8") as f:
        return json.load(f)


def test_local_catalog_is_a_lean_curated_subset():
    full = {t["name"]: t for t in _load("catalog.json")["tools"]}
    local = _load("catalog.local.json")["tools"]
    names = [t["name"] for t in local]

    # Small menu — the whole point (full is 29). Keep it in a sane range.
    assert 6 <= len(names) <= 16, len(names)
    assert len(names) == len(set(names)), "duplicate tool in local catalog"

    # Every local tool is an EXACT copy of the full catalog's entry (executor
    # blocks included), so the browser bridge + allowlist keep working.
    for t in local:
        assert t["name"] in full, f"{t['name']} not in full catalog"
        assert t == full[t["name"]], f"{t['name']} drifted from the full catalog"

    # Text-only model: no vision tools.
    assert not any("analyze" in n for n in names), names
    assert not any(full[n].get("returnsImage") for n in names), names

    # Both read-only and mutating tools present (so plan-approval is exercised).
    assert any(not full[n].get("requiresApproval") for n in names)
    assert any(full[n].get("requiresApproval") for n in names)

    # The RAG grounding tool is present — a small model needs it (without the
    # system prompt + knowledge, the bare 4B answered "why do flats matter?"
    # about apartments).
    assert "search_knowledge" in names


if __name__ == "__main__":
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            print("PASS", name)
    print("ALL PASS")
