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
# Headless test of the agent loop + plan-approval protocol, using the mock
# provider (no network, no browser). Proves the round-trip:
#   user -> (plan ->) tool-call intent -> "browser" returns result -> reply
#
#   python -m pytest server/test_agent.py     (or: python server/test_agent.py)

import asyncio
import os

os.environ["ASSISTANT_PROVIDER"] = "mock"

from agent import AgentSession, MAX_USER_CHARS, TURN_MAX  # noqa: E402
from providers import ToolCall  # noqa: E402


class JobHarness:
    """Bridge stand-in for background-job tools: answers the POST start with a
    jobId, then answers each status GET — InProgress:true for the first
    `pending` polls, then a done status."""

    def __init__(self, done_result, start_result=None, pending=1):
        self.out = []
        self.done_result = done_result
        self.start_result = start_result or {"jobId": "job1"}
        self.pending = pending
        self._status_calls = 0
        self.session = AgentSession(send=self._send)

    async def _send(self, m):
        self.out.append(m)
        if m.get("type") != "tool-call":
            return
        if m.get("method") == "GET" and "/status" in m.get("path", ""):
            self._status_calls += 1
            result = ({"inProgress": True, "stage": "running"}
                      if self._status_calls <= self.pending else self.done_result)
        else:
            result = self.start_result
        await self.session.on_message(
            {"type": "tool-result", "id": m["id"], "ok": True, "result": result})

    def status_calls(self):
        return self._status_calls


class Harness:
    """Drives an AgentSession, auto-answering every tool-call/ui intent with a
    fake OK result — standing in for the browser bridge + Polaris API."""

    def __init__(self):
        self.out = []
        self.session = AgentSession(send=self._send)

    async def _send(self, m):
        self.out.append(m)
        if m.get("type") in ("tool-call", "ui"):
            # The future is already registered before _send is awaited, so
            # resolving it here makes the agent's wait_for return immediately.
            await self.session.on_message(
                {"type": "tool-result", "id": m["id"], "ok": True, "result": {"stub": True}})

    def types(self):
        return [m["type"] for m in self.out]

    def first(self, t):
        return next((m for m in self.out if m["type"] == t), None)


def test_read_tool_runs_without_approval():
    h = Harness()
    asyncio.run(h.session.on_message({"type": "user", "text": "how is it going?"}))
    assert "tool-call" in h.types(), h.types()
    tc = h.first("tool-call")
    assert tc["path"] == "/api/system/status"
    assert h.types()[-1] == "done"
    assert any(m["type"] == "assistant" for m in h.out)


def test_ui_navigation_tool():
    h = Harness()
    asyncio.run(h.session.on_message({"type": "user", "text": "show me focus"}))
    ui = h.first("ui")
    assert ui is not None and ui["action"] == "navigate" and ui["params"]["tab"] == "focus"
    assert h.types()[-1] == "done"


def test_message_too_long_is_rejected():
    h = Harness()
    asyncio.run(h.session.on_message({"type": "user", "text": "x" * (MAX_USER_CHARS + 1)}))
    err = h.first("error")
    assert err is not None and err.get("code") == "too_long", h.out
    assert "tool-call" not in h.types()  # never reached the LLM/tools


def test_turn_rate_limit():
    h = Harness()
    # The window allows TURN_MAX turns; the next one is refused.
    for _ in range(TURN_MAX):
        assert h.session._allow_turn() is True
    assert h.session._allow_turn() is False


def test_mutating_tool_requires_plan_then_executes_on_approve():
    h = Harness()
    asyncio.run(h.session.on_message({"type": "user", "text": "slew to the eagle nebula"}))
    # A mutating action must propose a plan and NOT call the tool yet.
    assert "plan" in h.types(), h.types()
    assert "tool-call" not in h.types()
    plan = h.first("plan")
    assert plan["steps"] and plan["steps"][0]["mutates"] is True

    # Approve -> the tool now executes and the turn completes.
    asyncio.run(h.session.on_message({"type": "approve", "planId": plan["planId"]}))
    tc = h.first("tool-call")
    assert tc is not None and tc["path"] == "/api/sky/slew-and-center"
    assert tc["body"]["ra"] == 18.31
    assert h.types()[-1] == "done"


def test_reject_skips_the_tool():
    h = Harness()
    asyncio.run(h.session.on_message({"type": "user", "text": "slew somewhere"}))
    plan = h.first("plan")
    assert plan is not None
    asyncio.run(h.session.on_message({"type": "reject", "planId": plan["planId"], "reason": "no"}))
    # No slew tool-call was ever emitted.
    assert not any(m.get("path") == "/api/sky/slew-and-center" for m in h.out)
    assert h.types()[-1] == "done"


def test_poll_job_polls_until_inprogress_false():
    done = {"inProgress": False, "stage": "done",
            "selected": ["/a.fits", "/b.fits"], "selectedCount": 2}
    h = JobHarness(done_result=done, start_result={"jobId": "g1"}, pending=2)
    poll = {"statusPath": "/api/studio/grade/{jobId}/status",
            "doneField": "inProgress", "intervalMs": 5, "maxSeconds": 5}
    res = asyncio.run(h.session._poll_job(poll, "g1"))
    assert res["ok"] and res["result"]["selectedCount"] == 2, res
    # 2 in-progress polls + 1 final = 3 status calls, all against the job id.
    assert h.status_calls() == 3, h.status_calls()


def test_exec_tool_runs_job_then_returns_final_status():
    # integrate_frames is a poll tool: start returns {jobId}, then the agent
    # polls the integrate status endpoint and hands back the FINISHED status.
    done = {"inProgress": False, "stage": "done",
            "outputPath": "/rig/integrated/master.fits", "combined": 18, "dropped": 2}
    h = JobHarness(done_result=done, start_result={"jobId": "i9"}, pending=1)
    tc = ToolCall(id="mock-1", name="integrate_frames",
                  arguments={"framePaths": ["/a.fits", "/b.fits"]})
    res = asyncio.run(h.session._exec_tool("o1", tc))
    assert res["ok"] and res["result"]["stage"] == "done", res
    assert res["result"]["combined"] == 18
    # The start call carried the frame paths to the integrate endpoint.
    start = next(m for m in h.out if m.get("method") == "POST")
    assert start["path"] == "/api/studio/integrate"
    assert start["body"]["framePaths"] == ["/a.fits", "/b.fits"]


def test_analyze_current_view_captures_active_panel():
    # analyze_current_view must emit a `capture-view` intent (not an /api
    # tool-call) and return the on-screen image the host snapshots.
    out = []
    session = None

    async def send(m):
        out.append(m)
        if m.get("type") == "capture-view":
            await session.on_message({"type": "tool-result", "id": m["id"], "ok": True,
                                      "result": {"dataUrl": "data:image/jpeg;base64,AAAA",
                                                 "tab": "focus", "width": 800, "height": 600}})

    session = AgentSession(send=send)
    tc = ToolCall(id="mock-1", name="analyze_current_view", arguments={})
    res = asyncio.run(session._exec_tool("o1", tc))

    cv = next((m for m in out if m.get("type") == "capture-view"), None)
    assert cv is not None, out
    assert cv.get("maxDim") == 1536 and cv.get("quality") == 0.85
    # No /api tool-call was made for this (it's a client-side canvas grab).
    assert not any(m.get("type") == "tool-call" for m in out), out
    assert res["ok"] and res["result"]["dataUrl"].startswith("data:")
    assert res["result"]["tab"] == "focus"


def test_status_snapshots_emit_proactive_notice():
    # Two snapshots (baseline then guiding lost) should push a `notice` to the
    # client without any user turn or LLM call.
    out = []
    async def send(m):
        out.append(m)
    session = AgentSession(send=send)
    asyncio.run(session.on_message({"type": "status",
        "snapshot": {"guider": {"appState": "Guiding", "guiding": True}}}))
    assert not any(m["type"] == "notice" for m in out)  # baseline is silent
    asyncio.run(session.on_message({"type": "status",
        "snapshot": {"guider": {"appState": "LostLock", "guiding": True}}}))
    notice = next((m for m in out if m["type"] == "notice"), None)
    assert notice is not None and notice["key"] == "guiding_lost", out


if __name__ == "__main__":
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            print("PASS", name)
    print("ALL PASS")
