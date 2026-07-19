"""Capture real rig states from the Polaris simulator, to freeze into the eval.

WHY THIS EXISTS. The eval must be deterministic, so it replays frozen states
rather than driving a live rig. But hand-written states drift from the real
payload shape, and the day they drift the eval starts measuring the wrong thing:
the model reads a JSON the app will never send it. So the simulator produces the
states, and this script freezes them.

It also means the states carry the fields we would never think to invent, which
is exactly where a small model gets confused.

  # start Polaris, then launch the simulated gear:
  python capture.py --polaris http://localhost:5080 --token <token> --launch

  # capture into the file run_eval.py reads:
  python capture.py --polaris http://localhost:5080 --token <token> \
      --out scenarios/states.json

Endpoints used (verified against src/NINA.Polaris/Endpoints/):
  POST /api/simulator/launch            bring the simulated gear up
  POST /api/simulator/device/{tag}/start
  GET  /api/system/status               the state we freeze
  GET  /api/image/latest/stats?withStars=true
"""
from __future__ import annotations

import argparse
import json
import pathlib
import sys

import httpx

# Scenario id -> the rig condition it needs. Capture is best-effort per state:
# a Polaris with no simulated guider still yields a usable idle snapshot, and a
# partial capture beats a hand-written one.
WANTED = {
    "idle": ["rig_state", "resolve_target", "what_to_shoot", "explicit_slew"],
    "imaging": [
        "focus_question",
        "star_count",
        "explicit_autofocus",
        "trap_focus_complaint",
        "trap_conceptual",
    ],
    "guiding_lost": ["guiding_health"],
}


def _client(base: str, token: str | None) -> httpx.Client:
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    # verify=False: Polaris serves a self-signed cert on the LAN by default, the
    # same reason the app ships native LAN cert trust.
    return httpx.Client(base_url=base.rstrip("/"), headers=headers, timeout=30, verify=False)


def launch(c: httpx.Client) -> None:
    r = c.post("/api/simulator/launch")
    print(f"  launch -> {r.status_code}")
    for tag in ("camera", "mount", "focuser", "guider"):
        r = c.post(f"/api/simulator/device/{tag}/start")
        print(f"  {tag:8} -> {r.status_code}")


def snapshot(c: httpx.Client) -> dict:
    """One state. Trimmed: every field here is prompt tokens on a phone."""
    status = c.get("/api/system/status").json()
    state = {
        "equipment": status.get("equipment", {}),
        "sequence": status.get("sequence", {}),
        "liveStack": status.get("liveStack", {}),
    }
    if isinstance(status.get("guider"), dict):
        state["guider"] = status["guider"]
    return state


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--polaris", required=True, help="e.g. http://localhost:5080")
    ap.add_argument("--token", default=None, help="Polaris bearer token, if auth is on")
    ap.add_argument("--launch", action="store_true", help="Bring the simulated gear up first")
    ap.add_argument("--condition", default="idle", choices=sorted(WANTED))
    ap.add_argument("--out", type=pathlib.Path, default=pathlib.Path("scenarios/states.json"))
    args = ap.parse_args()

    with _client(args.polaris, args.token) as c:
        try:
            ident = c.get("/api/identify").json()
        except Exception as e:
            raise SystemExit(f"cannot reach Polaris at {args.polaris}: {e}")
        print(f"connected: {ident}")

        if args.launch:
            launch(c)

        state = snapshot(c)

    # Merge rather than overwrite: capturing one condition must not wipe the others.
    existing: dict = {}
    if args.out.exists():
        existing = json.loads(args.out.read_text(encoding="utf-8"))
    for sid in WANTED[args.condition]:
        existing[sid] = state

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(existing, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\ncaptured '{args.condition}' into {len(WANTED[args.condition])} scenario(s)")
    print(f"wrote {args.out}")
    print("\nNOTE: get the rig into the condition you want BEFORE capturing. This")
    print("script reads whatever state Polaris is in; it does not stage it for you.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
