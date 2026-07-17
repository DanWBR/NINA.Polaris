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
# Canopus Assistant — OPEN local serving layer.
#
#   uvicorn local_server:app --host 127.0.0.1 --port 8790
#
# The keyless, subscription-free analogue of the private cloud app.py, for the
# "On this server (SBC)" and (later) "On this device" backends. It serves:
#   - a LOCAL-tier manifest (tier:"local", NO subscription block),
#   - the open chat client + its bundled fonts,
#   - the agent WebSocket (no accounts, no billing, no entitlement gate).
#
# The agent loop (agent.py) uses whatever get_provider() returns; on the local
# tier that is the LlamaServerProvider (set CANOPUS_LOCAL_LLM_URL). Polaris runs
# this behind its own reverse-proxy at /canopus/*, so the client, manifest and
# WS are all same-origin as the Polaris page (loopback only).

from __future__ import annotations

import asyncio
import os

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles

from agent import AgentSession, CATALOG

_HERE = os.path.dirname(__file__)
CLIENT_DIR = os.path.join(_HERE, "..", "client")

# The base path Polaris mounts this app under (its reverse-proxy prefix). The
# manifest advertises absolute-under-prefix URLs so the FOSS host and the client
# both hit the proxied routes. Empty when running the app standalone on its root.
BASE_PATH = os.environ.get("CANOPUS_BASE_PATH", "/canopus").rstrip("/")

app = FastAPI(title="Canopus Assistant (local)")

# Same-origin in production (served under the Polaris proxy), but allow cross
# origin for standalone dev where the FOSS host points a manifest URL here.
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["GET", "POST", "OPTIONS"],
    allow_headers=["*"],
)


def _build_allowlist() -> list[dict]:
    """Derive the tool-call allowlist from the shared catalog so it can never
    drift from the tools the agent actually offers. Every Polaris endpoint a tool
    can reach — its action (`polaris`), any image fetch (`image`), and any job
    poll (`poll`) — becomes an allow entry. The FOSS host enforces this list (plus
    a hardcoded denylist) before executing any call the agent requests."""
    catalog = CATALOG   # the tier-correct catalog the agent actually offers
    seen: set[tuple[str, str]] = set()
    allow: list[dict] = []

    def add(method: str, path: str) -> None:
        if not method or not path:
            return
        key = (method.upper(), path)
        if key not in seen:
            seen.add(key)
            allow.append({"method": method.upper(), "pathPattern": path})

    for t in catalog.get("tools", []):
        p = t.get("polaris")
        if isinstance(p, dict):
            add(p.get("method", "GET"), p.get("path"))
        img = t.get("image")
        if isinstance(img, dict):
            add(img.get("method", "GET"), img.get("path"))
        poll = t.get("poll")
        if isinstance(poll, dict):
            add("GET", poll.get("statusPath"))
    return allow


_ALLOWLIST = _build_allowlist()


# Served at BOTH paths: the FOSS host fetches /manifest.json, while the chat
# client's boot() fetches {api.base}/manifest (i.e. /api/manifest). Both must
# return the tier:"local" manifest so the client skips the cloud sign-in gate.
@app.get("/manifest.json")
@app.get("/api/manifest")
def manifest() -> JSONResponse:
    """A LOCAL-tier manifest. No `subscription` block — the FOSS host reveals the
    chat directly (local = free). iframe + api are relative to the proxy prefix."""
    return JSONResponse({
        "version": 1,
        "tier": "local",
        "product": {"name": "Canopus (local)", "tagline": "On-device AI observing companion", "iconEmoji": "🔭"},
        "intro": {
            "headline": "Your rig's own AI, running locally",
            "bodyMarkdown": "Canopus runs entirely on this server — no cloud, no account, "
                            "no subscription. It plans the night, drives the rig with your "
                            "approval, and answers questions, all offline.",
            "bullets": [
                "\"What's good tonight? Suggest a plan.\"",
                "\"How is it going?\"",
                "\"Show me focus.\"",
            ],
        },
        "iframe": {
            "url": f"{BASE_PATH}/app",
            "origin": None,   # same-origin as the Polaris page; host uses its own origin
            "sandbox": "allow-scripts allow-forms allow-popups allow-same-origin allow-modals",
            "fabIconEmoji": "🔭",
            "fabLabel": "Canopus",
        },
        "api": {"base": f"{BASE_PATH}/api"},
        "allowlist": _ALLOWLIST,
    })


@app.get("/app")
def client_app() -> FileResponse:
    return FileResponse(os.path.join(CLIENT_DIR, "index.html"))


# The bundled UI fonts (same origin as the chat iframe → no CORS), so the chat
# renders in the exact typeface the user picked in Polaris.
app.mount("/fonts", StaticFiles(directory=os.path.join(CLIENT_DIR, "fonts")), name="fonts")


@app.get("/healthz")
def healthz() -> dict:
    return {"ok": True, "tier": "local"}


@app.websocket("/api/agent")
async def agent_ws(ws: WebSocket) -> None:
    """The agent conversation. Unlike the cloud, there is NO entitlement gate:
    the local tier is free. One AgentSession per connection; each inbound message
    is dispatched as its own task so a `user`/`approve` turn (which blocks
    awaiting a tool-call's `tool-result` from a LATER message) can't deadlock the
    receive loop."""
    await ws.accept()
    session = AgentSession(send=ws.send_json)
    tasks: set = set()
    try:
        while True:
            msg = await ws.receive_json()
            if not isinstance(msg, dict):
                continue
            t = asyncio.create_task(session.on_message(msg))
            tasks.add(t)
            t.add_done_callback(tasks.discard)
    except WebSocketDisconnect:
        for t in tasks:
            t.cancel()
