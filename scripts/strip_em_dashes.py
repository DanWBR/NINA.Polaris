"""
PA-8: one-shot em-dash purge.

User asked: "substitua todos os usos do caractere — por vírgula,
dois pontos ou ponto final ou ponto e vírgula. E nunca mais use
esse — em lugar nenhum."

Heuristic:
  ' — '  →  ', '     (most common: parenthetical aside / coordinating clause)
  ' —'   →  ','      (em-dash at end of word)
  '— '   →  ', '     (em-dash at start of word, possibly line wrap)
  '—'    →  ', '     (fallback: any remaining bare em-dash)

Comma is grammatically valid almost everywhere em-dash works. May
lose a bit of emphasis but reads correctly. The user can adjust
specific spots to colon/period/semicolon by hand later if a comma
feels weak.

Targets the files the user explicitly cares about: UI (index.html,
app.js, onnx-pipelines.js, app.css), docs (every .md under docs/),
top-level README + PLAN, AND C# source files (their comments
count as docs too).

Skips third-party / vendored / binary content: anything under
wwwroot/js/lib, wwwroot/sky (stellarium-web AGPL), wwwroot/graxpert
(NASA / GraXpert assets), external/, node_modules/, bin/, obj/,
nanobanana-output/.

Idempotent. Re-runnable. Prints a per-file count so you can see the
damage.
"""
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EM = "—"   # U+2014 EM DASH

SKIP_FRAGMENTS = (
    "wwwroot/js/lib/",
    "wwwroot/sky/",
    "wwwroot/graxpert/",
    "nanobanana-output/",
    "external/",
    "node_modules/",
    "/bin/",
    "/obj/",
)

EXTENSIONS = {".html", ".js", ".css", ".md", ".cs"}

def should_skip(rel_posix: str) -> bool:
    return any(frag in rel_posix for frag in SKIP_FRAGMENTS)

def fix(text: str) -> str:
    text = text.replace(f" {EM} ", ", ")
    text = text.replace(f" {EM}", ",")
    text = text.replace(f"{EM} ", ", ")
    text = text.replace(EM, ", ")
    return text

def main():
    total_files = 0
    total_repls = 0
    touched = []
    for path in ROOT.rglob("*"):
        if not path.is_file():
            continue
        if path.suffix.lower() not in EXTENSIONS:
            continue
        rel = path.relative_to(ROOT).as_posix()
        if should_skip(rel):
            continue
        try:
            original = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        if EM not in original:
            continue
        count = original.count(EM)
        new = fix(original)
        if new == original:
            continue
        path.write_text(new, encoding="utf-8")
        total_files += 1
        total_repls += count
        touched.append((rel, count))

    touched.sort(key=lambda t: -t[1])
    for rel, count in touched:
        print(f"  {count:4d}  {rel}")
    print(f"\nReplaced {total_repls} em-dashes across {total_files} files.")

if __name__ == "__main__":
    main()
