"""Run the multi-step workflow eval: sequences and nested-argument tools.

Companion to run_eval.py. Where that scores one tool call per turn, this drives
the model through workflows that need several (run autofocus then evaluate; start
live stacking then read the stack) plus the nested-args create_plan.

  python run_multistep.py --backend mock
  python run_multistep.py --backend transformers --model Qwen/Qwen3.5-4B
  python run_multistep.py --backend transformers --model google/gemma-4-e4b-it --load-4bit
"""
from __future__ import annotations

import argparse
import json
import pathlib
import sys

from canopus_eval.backends import make_backend
from canopus_eval.multistep import MULTI, run_multistep, score_multistep


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--backend", default="mock", choices=["mock", "transformers", "ortgenai"])
    ap.add_argument("--model", default="keyword-router")
    ap.add_argument("--think", action="store_true")
    ap.add_argument("--max-new-tokens", type=int, default=1024)
    ap.add_argument("--load-4bit", action="store_true")
    ap.add_argument("--max-steps", type=int, default=5)
    ap.add_argument("--out", type=pathlib.Path, default=None)
    args = ap.parse_args()

    kw = {}
    if args.backend == "transformers":
        kw = {"think": args.think, "max_new_tokens": args.max_new_tokens, "load_4bit": args.load_4bit}
    backend = make_backend(args.backend, args.model, **kw)

    outcomes = []
    for s in MULTI:
        tr = run_multistep(backend, s, max_steps=args.max_steps)
        o = score_multistep(s, tr)
        outcomes.append(o)
        mark = "ok  " if o.passed else ("UNSAFE" if o.unsafe else "fail")
        seq = " -> ".join(o.calls) or "(no calls)"
        print(f"  [{mark:6}] {s.id:24} steps={o.steps} calls: {seq}")
        if not o.passed:
            print(f"            {o.note}")

    n = len(outcomes) or 1
    summary = {
        "model": args.model,
        "backend": backend.name,
        "n": len(outcomes),
        "pass_rate": round(sum(o.passed for o in outcomes) / n, 3),
        "ordered_rate": round(sum(o.ordered_ok for o in outcomes) / n, 3),
        "unsafe_calls": sum(len(o.unsafe) for o in outcomes),
        "nested_ok": all(o.nested_ok for o in outcomes),
        "avg_steps": round(sum(o.steps for o in outcomes) / n, 1),
        "total_ms_median": sorted(o.total_ms for o in outcomes)[len(outcomes) // 2] if outcomes else 0,
    }
    print("\n" + json.dumps(summary, indent=2, ensure_ascii=False))

    out = args.out or pathlib.Path("eval") / f"canopus_multistep_{backend.name}_{_slug(args.model)}.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(
            {
                "summary": summary,
                "outcomes": [
                    {
                        "id": o.scenario_id, "expected": o.expected, "calls": o.calls,
                        "passed": o.passed, "unsafe": o.unsafe, "steps": o.steps, "note": o.note,
                    }
                    for o in outcomes
                ],
            },
            indent=2,
            ensure_ascii=False,
        ),
        encoding="utf-8",
    )
    print(f"wrote {out}")
    return 1 if summary["unsafe_calls"] else 0


def _slug(s: str) -> str:
    return "".join(c if c.isalnum() else "-" for c in s.lower()).strip("-")


if __name__ == "__main__":
    sys.exit(main())
