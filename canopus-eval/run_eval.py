"""Run the Canopus tool-calling eval and write a report.

This is the Phase 0 deliverable. It answers one question: can a stock small model
pick the right tool for an astrophotography turn, and can it keep its hands off
the mount when nobody asked. If it can, no training happens.

  # sanity-check the harness (no model, no GPU)
  python run_eval.py --backend mock

  # pick a model, on the desktop
  python run_eval.py --backend transformers --model Qwen/Qwen3-4B
  python run_eval.py --backend transformers --model google/gemma-4-e4b-it

  # the number that closes Phase 0: on a phone, not here
  python run_eval.py --backend ortgenai --model ./models/qwen3-4b-int4

Reports land in eval/ as JSON, matching polaris-ai's convention.
"""
from __future__ import annotations

import argparse
import json
import pathlib
import sys

from canopus_eval.backends import make_backend
from canopus_eval.scenarios import SCENARIOS, Scenario, coverage_gaps
from canopus_eval.score import Report, score_one


def load_states(path: pathlib.Path) -> dict[str, dict]:
    """Swap in states captured from the simulator, keyed by scenario id."""
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def apply_states(scenarios: list[Scenario], states: dict[str, dict]) -> list[Scenario]:
    import dataclasses

    out = []
    for s in scenarios:
        st = states.get(s.id)
        out.append(dataclasses.replace(s, state=st) if st else s)
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--backend", default="mock", choices=["mock", "transformers", "ortgenai"])
    ap.add_argument("--model", default="keyword-router")
    ap.add_argument("--states", type=pathlib.Path, default=pathlib.Path("scenarios/states.json"))
    ap.add_argument("--out", type=pathlib.Path, default=None)
    ap.add_argument("--only", default=None, help="Run one scenario by id.")
    ap.add_argument(
        "--think",
        action="store_true",
        help="Let a reasoning model think first (Qwen3). Off by default: it cost "
        "13s median on a 5070 Ti, and the target is a phone.",
    )
    ap.add_argument("--max-new-tokens", type=int, default=1024)
    ap.add_argument(
        "--load-4bit",
        action="store_true",
        help="4-bit NF4 load. Needed for the multimodal E4B models (~16GB in bf16, "
        "OOM on 16GB), and closer to the on-device int4 target than bf16.",
    )
    args = ap.parse_args()

    # Report holes before spending a GPU on a set that does not cover the catalog.
    # A green run over a set with gaps is worth less than a red run over a full one.
    for gap in coverage_gaps():
        print(f"coverage gap: {gap}")

    scenarios = SCENARIOS
    if args.only:
        scenarios = [s for s in scenarios if s.id == args.only]
        if not scenarios:
            raise SystemExit(f"no scenario with id {args.only!r}")

    states = load_states(args.states)
    if states:
        scenarios = apply_states(scenarios, states)
        print(f"using {len(states)} captured state(s) from {args.states}")
    else:
        print(f"no captured states at {args.states}, using the built-in placeholders")

    kw = {}
    if args.backend == "transformers":
        kw = {
            "think": args.think,
            "max_new_tokens": args.max_new_tokens,
            "load_4bit": args.load_4bit,
        }
    backend = make_backend(args.backend, args.model, **kw)
    report = Report(model=args.model, backend=backend.name)
    if args.backend == "transformers":
        prec = "4bit" if args.load_4bit else "bf16"
        print(f"thinking={'on' if args.think else 'off'}, {prec}, max_new_tokens={args.max_new_tokens}")

    for s in scenarios:
        call, raw, ms = backend.generate(s)
        outcome = score_one(s, call, ms, raw)
        report.outcomes.append(outcome)
        mark = "ok  " if outcome.passed else ("UNSAFE" if outcome.unsafe else "fail")
        got = outcome.got or "(no tool)"
        print(f"  [{mark:6}] {s.id:24} expected={s.expect_tool or '(none)':18} got={got}")

    summary = report.summary()
    print("\n" + json.dumps(summary, indent=2, ensure_ascii=False))

    if report.failures():
        print("\nfailures:")
        for f in report.failures():
            print(f"  {f['id']}: {f['note'] or f'expected {f['expected']}, got {f['got']}'}")

    out = args.out or pathlib.Path("eval") / f"canopus_{backend.name}_{_slug(args.model)}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(report.to_json(), encoding="utf-8")
    print(f"\nwrote {out}")

    # Exit non-zero on any unsafe call so CI can gate on it. Accuracy is a number
    # to improve; an unprompted slew is a bug to fix.
    return 1 if summary["unsafe_calls"] else 0


def _slug(s: str) -> str:
    return "".join(c if c.isalnum() else "-" for c in s.lower()).strip("-")


if __name__ == "__main__":
    sys.exit(main())
