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
# Tests for the OPEN local serving layer: the local-tier manifest shape (no
# subscription gate) and a full agent WebSocket round-trip against the mock
# provider (no llama-server, no keys).
#
#   python -m pytest server/test_local_server.py

import os

# The agent must use the deterministic mock; set before local_server imports agent.
os.environ["ASSISTANT_PROVIDER"] = "mock"

from fastapi.testclient import TestClient  # noqa: E402

import local_server  # noqa: E402
from local_server import app, BASE_PATH  # noqa: E402

client = TestClient(app)


def test_manifest_is_local_tier_without_subscription():
    m = client.get("/manifest.json").json()
    assert m["version"] == 1
    assert m["tier"] == "local"
    # Local tier is free: NO subscription block (the FOSS host reveals the FAB
    # directly instead of showing a subscribe wall).
    assert "subscription" not in m
    assert m["iframe"]["url"] == f"{BASE_PATH}/app"
    assert m["api"]["base"] == f"{BASE_PATH}/api"


def test_manifest_allowlist_derived_from_catalog():
    allow = client.get("/manifest.json").json()["allowlist"]
    pairs = {(a["method"], a["pathPattern"]) for a in allow}
    # A read tool, a mutating tool, an image fetch, and a job-poll path all made it.
    assert ("GET", "/api/system/status") in pairs
    assert ("POST", "/api/sky/slew-and-center") in pairs
    assert ("GET", "/api/image/latest/preview") in pairs
    assert ("GET", "/api/studio/grade/{jobId}/status") in pairs
    # Denylisted / non-API endpoints never appear.
    assert not any(p.startswith("/api/auth") for _, p in pairs)


def test_healthz():
    assert client.get("/healthz").json()["ok"] is True


def test_agent_ws_round_trip_with_mock():
    with client.websocket_connect("/api/agent") as ws:
        ws.send_json({"type": "hello", "locale": "en"})
        ws.send_json({"type": "user", "text": "how is it going?"})
        seen = []
        for _ in range(30):
            m = ws.receive_json()
            seen.append(m)
            if m.get("type") == "tool-call":
                ws.send_json({"type": "tool-result", "id": m["id"], "ok": True,
                              "result": {"stub": True}})
            if m.get("type") == "done":
                break
        types = [m["type"] for m in seen]
        assert "tool-call" in types, types          # mock routes "going" -> get_status
        assert "assistant" in types, types
        assert types[-1] == "done", types
        tc = next(m for m in seen if m["type"] == "tool-call")
        assert tc["path"] == "/api/system/status"


if __name__ == "__main__":
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            print("PASS", name)
    print("ALL PASS")
