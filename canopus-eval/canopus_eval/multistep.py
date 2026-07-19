"""Multi-step eval: workflows that need more than one tool call.

Single-turn tool choice is largely solved (0.90-0.95 across three models). The
harder, more representative question is whether a small model can drive a
SEQUENCE: run autofocus, look at the result, then decide to measure it; start a
live stack, then read back the stack. And whether it can build the deeply nested
arguments a real create_plan needs. That is what this module tests.

HOW THE LOOP WORKS. The harness plays the rest of the rig. The model emits a tool
call; we look up a canned result for that tool (the fixtures below), feed it back,
and let the model take the next step, up to a step cap. Then we score the whole
sequence of calls, not a single one.

WHY CANNED RESULTS, not the live simulator: same reason the single-turn eval
freezes states. The result of "run autofocus" has to be identical every run or the
model's next step is judged against a moving target. The fixtures are shaped like
the real endpoint responses so the model reads what the app would actually send.

WHAT COUNTS AS SUCCESS. A sequence passes when it contains the expected tools in an
acceptable order and adds no unsafe call. "Acceptable order" is deliberately loose:
if the expected sequence is [start_live_stack, get_live_stack], a model that also
calls get_status first is fine; one that reads the stack before starting it is not;
one that never starts it has not done the task. Unsafe calls (a mutating tool the workflow
did not call for) fail the sequence outright, same rule as single-turn.
"""
from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any

from .catalog import MUTATING


# ---------------------------------------------------------------------------
# Tool-result fixtures. Shaped like the real endpoint responses. A tool with no
# fixture returns a generic ok, which is enough for tools whose result the model
# does not need to reason about (start_guiding just needs to have happened).
# ---------------------------------------------------------------------------

FIXTURES: dict[str, Any] = {
    # The composite tool's result is self-complete: it carries the evaluation the
    # two-step workflow needed a second call to get. This is the whole point of
    # the composite lever, expressed as data.
    "autofocus": {
        "status": "completed", "focuserPosition": 18402,
        "hfrBefore": 3.6, "hfrAfter": 2.85, "starCount": 151, "improved": True,
    },
    "get_image_stats": {
        "medianHfr": 2.85, "starCount": 148, "meanHfr": 3.02,
        "background": 512, "noise": 11.4,
    },
    "start_live_stack": {"status": "started", "exposure": 30},
    "get_live_stack": {"isRunning": True, "frameCount": 6, "integrationSec": 180, "medianHfr": 2.9},
    "start_guiding": {"status": "guiding", "rmsTotalPx": 0.58},
    "get_weather": {"cloudCover": 8, "rainRate": 0, "humidity": 61, "safe": True},
    "get_forecast": {
        "nights": [{"date": "tonight", "cloudPct": 12, "seeing": "good", "score": 0.82}]
    },
    "get_tonights_best": {
        "targets": [
            {"name": "M31", "altitude": 63, "score": 0.91},
            {"name": "NGC 7000", "altitude": 48, "score": 0.85},
        ]
    },
    "search_catalog": {"name": "M31", "raHours": 0.712, "decDeg": 41.27, "sizeArcmin": 190},
    "get_status": {
        "mount": {"tracking": True, "ra": 0.712, "dec": 41.27}, "minutesToMeridian": 74,
        "sequence": {"running": True, "frame": 8, "total": 40},
    },
    "create_plan": {"id": "plan_42", "status": "draft", "targetCount": 1},
    "slew_to": {"status": "centered", "errorArcsec": 6},
}


def fixture_for(tool: str) -> dict:
    return FIXTURES.get(tool, {"ok": True})


# ---------------------------------------------------------------------------
# Scenarios. `expect_sequence` is the tools the workflow requires, in order. The
# runner allows extra read-only calls interleaved; it forbids extra mutating ones.
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class MultiScenario:
    id: str
    user: str
    state: dict[str, Any]
    expect_sequence: list[str]
    intent: str = ""
    # For create_plan: the arg path that must be present and well-formed, checked
    # structurally rather than by value (a 4B will not match exact exposures).
    require_nested: bool = False
    # Mutating tools acceptable for this workflow beyond the strict sequence.
    # Defaults to the mutating tools in expect_sequence. Widen it when two tools are
    # the same KIND of action, so choosing a legitimate variant is not scored as
    # unsafe. Currently unused (the catalog was consolidated so no such near-
    # duplicates remain), kept as the general mechanism for when they reappear.
    allow_mutating: frozenset[str] | None = None


# Minimal running state; the compact_state() reducer trims it at prompt time.
_IMAGING = {
    "equipment": {
        "camera": {"connected": True}, "mount": {"connected": True, "tracking": True},
        "focuser": {"connected": True}, "guider": {"connected": True, "state": "Guiding"},
    },
    "sequence": {"running": True}, "liveStack": {"isRunning": False},
}
_IDLE = {
    "equipment": {
        "camera": {"connected": True}, "mount": {"connected": True, "parked": True},
        "focuser": {"connected": True}, "guider": {"connected": False},
    },
    "sequence": {"running": False}, "liveStack": {"isRunning": False},
}


MULTI: list[MultiScenario] = [
    # The autofocus story reached its endpoint and the catalog was consolidated:
    #   - The two-step run_autofocus + get_image_stats workflow showed small models
    #     drop the second step.
    #   - A composite `autofocus` tool fixed it, and both models preferred it.
    #   - Offered both the primitive and the composite, both models ignored the
    #     primitive entirely (the autofocus_bare finding).
    # So run_autofocus was removed. One `autofocus` tool remains, and one scenario:
    # the model calls it once and reports from the self-complete result. The
    # before/after of this experiment lives in the README, not in dead scenarios.
    MultiScenario(
        id="autofocus_composite",
        user="roda o autofoco e me diz se ficou bom",
        state=_IMAGING,
        expect_sequence=["autofocus"],
        intent="The consolidated autofocus: one composite call whose result carries "
        "before/after HFR, so there is no second step for a small model to drop.",
    ),
    MultiScenario(
        id="livestack_then_return",
        user="inicia o live stacking e me mostra como está o stack",
        state=_IMAGING,
        expect_sequence=["start_live_stack", "get_live_stack"],
        intent="Start something, then read it back. Tests that the model does not "
        "treat 'start and show' as a single call, and that it reads the "
        "stack rather than claiming a frame count it cannot know.",
    ),
    MultiScenario(
        id="plan_a_night",
        user="monta um plano pra fotografar M31 hoje, umas 40 poses de 3 minutos",
        state=_IDLE,
        expect_sequence=["create_plan"],
        require_nested=True,
        intent="The nested-args stress test. One call, but the argument is a "
        "targets array holding a frames array. Small models tend to flatten "
        "it or drop the inner list. 40x180s on M31 is enough signal to check "
        "the structure without demanding exact values.",
    ),
    MultiScenario(
        id="start_guiding_simple",
        # State must have guiding OFF, or "start guiding" has no work to do. The
        # first version used the imaging state where guiding was already on, and
        # Gemma correctly refused ("guiding is already active"), a scenario bug
        # scored as a model failure. _IDLE has the guider disconnected, so the
        # ask is real.
        user="conecta e começa a guiar",
        state=_IDLE,
        expect_sequence=["start_guiding"],
        intent="A single mutating call, new to the catalog and easy to confuse "
        "with dither. State has guiding off so the request is actionable.",
    ),
    MultiScenario(
        id="forecast_planning",
        user="como fica o tempo pras próximas noites?",
        state=_IDLE,
        expect_sequence=["get_forecast"],
        intent="Forecast, not current conditions: get_forecast vs get_weather. "
        "'Próximas noites' is future, so the sensor reading is the wrong tool.",
    ),
]

MULTI_BY_ID: dict[str, MultiScenario] = {s.id: s for s in MULTI}


# ---------------------------------------------------------------------------
# Runner + scoring.
# ---------------------------------------------------------------------------

@dataclass
class StepTrace:
    calls: list[str] = field(default_factory=list)
    call_args: list[dict] = field(default_factory=list)
    steps: int = 0
    total_ms: int = 0
    transcript: list[str] = field(default_factory=list)
    unsafe: list[str] = field(default_factory=list)


def run_multistep(backend, scenario: MultiScenario, max_steps: int = 5) -> StepTrace:
    """Drive the model through the workflow, feeding canned results back.

    Uses the backend's run_messages() and grows a messages list. The assistant's
    raw output goes back verbatim; the tool result goes back as a `tool` role,
    which the Qwen and Gemma templates render natively. If a step yields no call,
    the model considers itself done and the loop ends.
    """
    from .backends import build_messages

    allowed_mutating = (
        set(scenario.allow_mutating)
        if scenario.allow_mutating is not None
        else set(scenario.expect_sequence) & MUTATING
    )
    msgs = build_messages(scenario)  # system + first user turn
    tr = StepTrace()

    for _ in range(max_steps):
        call, raw, ms = backend.run_messages(msgs)
        tr.steps += 1
        tr.total_ms += ms
        tr.transcript.append(raw)
        if call is None:
            break  # model answered with text: workflow finished (or gave up)
        name, args = call
        tr.calls.append(name)
        tr.call_args.append(args)
        # An unsafe call is a mutating tool the workflow never asked for. Same
        # definition as single-turn, applied to each step.
        if name in MUTATING and name not in allowed_mutating:
            tr.unsafe.append(name)
        # Feed the result back as a plain user message, NOT a `role: tool` message.
        # This is a fairness fix, not a style choice: Qwen3.5's template renders a
        # tool-role result into the prompt, but Gemma's DROPS the content entirely,
        # so Gemma was being fed results it could not see. It "failed" autofocus
        # (narrated "will report once complete" instead of reading hfrAfter) purely
        # because the completed result never reached it. A user message renders
        # verbatim in both templates, so both models see the same result. Slightly
        # less native, but the comparison is only meaningful if both can read.
        result = json.dumps(fixture_for(name), ensure_ascii=False)
        msgs = msgs + [
            {"role": "assistant", "content": raw},
            {"role": "user", "content": f"[tool result for {name}]\n{result}"},
        ]
    return tr


def _nested_ok(args: dict) -> tuple[bool, str]:
    """create_plan structural check: targets[] each with a non-empty frames[]."""
    targets = args.get("targets")
    if not isinstance(targets, list) or not targets:
        return False, "targets missing or not a non-empty array"
    for t in targets:
        if not isinstance(t, dict) or "name" not in t:
            return False, "a target is not an object with a name"
        frames = t.get("frames")
        if not isinstance(frames, list) or not frames:
            return False, f"target {t.get('name')!r} has no frames array"
        if not all(isinstance(fr, dict) and "exposureSeconds" in fr for fr in frames):
            return False, "a frame is missing exposureSeconds"
    return True, ""


@dataclass
class MultiOutcome:
    scenario_id: str
    expected: list[str]
    calls: list[str]
    ordered_ok: bool
    nested_ok: bool
    unsafe: list[str]
    steps: int
    total_ms: int
    note: str = ""

    @property
    def passed(self) -> bool:
        return self.ordered_ok and self.nested_ok and not self.unsafe


def _subsequence(expected: list[str], calls: list[str]) -> bool:
    """Every expected tool appears, in order, allowing extra reads in between."""
    it = iter(calls)
    return all(tool in it for tool in expected)


def score_multistep(scenario: MultiScenario, tr: StepTrace) -> MultiOutcome:
    ordered = _subsequence(scenario.expect_sequence, tr.calls)
    nested_ok, nmsg = (True, "")
    if scenario.require_nested and "create_plan" in tr.calls:
        i = tr.calls.index("create_plan")
        nested_ok, nmsg = _nested_ok(tr.call_args[i])
    elif scenario.require_nested:
        nested_ok, nmsg = False, "create_plan never called"

    notes = []
    if not ordered:
        notes.append(f"expected {scenario.expect_sequence} in order, got {tr.calls}")
    if nmsg:
        notes.append(nmsg)
    if tr.unsafe:
        notes.append(f"UNSAFE extra mutating call(s): {tr.unsafe}")

    return MultiOutcome(
        scenario_id=scenario.id,
        expected=scenario.expect_sequence,
        calls=tr.calls,
        ordered_ok=ordered,
        nested_ok=nested_ok,
        unsafe=tr.unsafe,
        steps=tr.steps,
        total_ms=tr.total_ms,
        note="; ".join(notes),
    )
