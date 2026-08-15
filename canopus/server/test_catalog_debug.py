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
# Guards the read-only DEBUG tools (CANOPUS-DEBUG-1): the assistant can read
# logs/files/FITS headers to diagnose problems, but these must stay read-only
# (no mutation, no approval gate) and map to GET endpoints only.

import json
import os

_TOOLS_DIR = os.path.join(os.path.dirname(__file__), "..", "shared", "tools")

DEBUG_TOOLS = {"read_logs", "get_studio_root", "list_dir", "read_file", "get_fits_headers"}


def _full():
    with open(os.path.join(_TOOLS_DIR, "catalog.json"), "r", encoding="utf-8") as f:
        return {t["name"]: t for t in json.load(f)["tools"]}


def test_debug_tools_present_and_read_only():
    full = _full()
    for name in DEBUG_TOOLS:
        assert name in full, f"{name} missing from catalog.json"
        t = full[name]
        assert t.get("mutates") is False, f"{name} must not mutate"
        assert t.get("requiresApproval") is False, f"{name} must not require approval"
        pol = t.get("polaris") or {}
        assert pol.get("method") == "GET", f"{name} must map to a GET endpoint"
        assert pol.get("path", "").startswith("/api/"), f"{name} needs a real API path"


def test_read_file_maps_to_the_guarded_text_endpoint():
    # read_file must go through /api/files/read-text (the secret-guarded, size-
    # capped reader), never /download (raw bytes, no secret guard).
    full = _full()
    assert full["read_file"]["polaris"]["path"] == "/api/files/read-text"
    assert full["read_logs"]["polaris"]["path"] == "/api/logs"


if __name__ == "__main__":
    for _n, _fn in sorted(globals().items()):
        if _n.startswith("test_") and callable(_fn):
            _fn()
            print("PASS", _n)
    print("ALL PASS")
