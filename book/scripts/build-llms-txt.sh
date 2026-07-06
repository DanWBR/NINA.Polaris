#!/usr/bin/env bash
# Build an LLM-ingestible plain-text edition of The Polaris Handbook.
#
# Produces two files under _build/, following the emerging llms.txt
# convention (https://llmstxt.org):
#
#   llms.txt       a short index: title, summary, and the chapter list
#   llms-full.txt  the entire book as one lightly-cleaned Markdown file
#
# These are published under /handbook/ by deploy-website.yml so the
# Polaris Assistant (an external service) or any LLM tool can fetch and
# index the manual from a stable URL.
#
# The cleanup strips Quarto-only markup that is noise to a reader:
# heading/figure anchors, callout fences, citation and cross-reference
# markers, and HTML comments; inline footnotes become parentheticals and
# figures become "Figure:" lines. It is deliberately source-based (no
# render needed), so it stays fast and dependency-free in CI.

set -euo pipefail

cd "$(dirname "$0")/.."   # book/
OUT_DIR="_build"
mkdir -p "$OUT_DIR"
FULL="$OUT_DIR/llms-full.txt"
INDEX="$OUT_DIR/llms.txt"

# Ordered list of source files and part headings, parsed from _quarto.yml
# so this never drifts from the book structure. Emits lines of the form
# "PART<TAB>Title" or "FILE<TAB>name.qmd".
manifest() {
  awk '
    /^book:/        { inbook = 1; next }
    /^[a-z].*:/     { if (inbook && !/^  / ) inbook = 0 }
    inbook && /^[[:space:]]*- part:/ {
      line = $0
      sub(/^[[:space:]]*- part:[[:space:]]*"?/, "", line)
      sub(/"?[[:space:]]*$/, "", line)
      print "PART\t" line
      next
    }
    inbook && /^  appendices:/ { print "PART\tAppendices"; next }
    inbook && /\.qmd[[:space:]]*$/ {
      f = $0
      sub(/^[[:space:]]*-[[:space:]]*/, "", f)
      print "FILE\t" f
    }
  ' _quarto.yml
}

# Light Markdown cleanup for one chapter file (stdin -> stdout).
clean() {
  perl -0777 -pe '
    s/<!--.*?-->//gs;                         # HTML comments (screenshots, trailers)
    s/\^\[([^\]]*)\]/ ($1)/g;                 # inline footnotes -> parentheticals
    s/!\[([^\]]*)\]\([^)]*\)(\{[^}]*\})?/Figure: $1/g;  # images -> Figure: caption
    s/\s*\((?:see\s+)?@(?:sec|fig|tbl)-[a-z0-9-]+\)//g; # (@sec-x) parentheticals
    s/@(?:sec|fig|tbl)-[a-z0-9-]+/another section/g;    # bare @sec-x refs
    s/\s*\[@[^\]]+\]//g;                      # [@citation] markers
    s/\{#[^}]*\}//g;                          # {#sec-x} / {#fig-x} anchors
    s/\{\.[^}]*\}//g;                          # {.unnumbered} etc
  ' \
  | sed -E '/^:::/d'                           # callout / div fences
}

# ---- llms-full.txt ----
{
  echo "# The Polaris Handbook"
  echo
  echo "Browser-controlled astrophotography with N.I.N.A. Polaris."
  echo "Complete manual, plain-text edition for machine reading."
  echo "Source: https://github.com/DanWBR/NINA.Polaris (book/)."
  echo "Human-readable editions: https://polaris-astro.app.br/handbook/ (web),"
  echo "and The-Polaris-Handbook.pdf on each GitHub release."
  echo
} > "$FULL"

while IFS=$'\t' read -r kind value; do
  case "$kind" in
    PART)
      { echo; echo; echo "# Part: $value"; echo; } >> "$FULL"
      ;;
    FILE)
      [ "$value" = "99-references.qmd" ] && continue   # empty #refs placeholder
      [ -f "$value" ] || { echo "missing: $value" >&2; exit 1; }
      { echo; clean < "$value"; echo; } >> "$FULL"
      ;;
  esac
done < <(manifest)

# ---- llms.txt (index) ----
{
  echo "# The Polaris Handbook"
  echo
  echo "> The single reference for N.I.N.A. Polaris, a browser-controlled"
  echo "> astrophotography system: installation, equipment, capture,"
  echo "> processing, remote access, and internals."
  echo
  echo "- Full text: llms-full.txt"
  echo "- Web edition: https://polaris-astro.app.br/handbook/"
  echo
  echo "## Chapters"
  echo
  while IFS=$'\t' read -r kind value; do
    case "$kind" in
      PART) echo; echo "### $value" ;;
      FILE)
        [ "$value" = "99-references.qmd" ] && continue
        # first line of the file is "# Title {#sec-...}"
        title=$(head -1 "$value" | sed -E 's/^#+[[:space:]]*//; s/[[:space:]]*\{[^}]*\}[[:space:]]*$//')
        echo "- $title"
        ;;
    esac
  done < <(manifest)
} > "$INDEX"

echo "Wrote $FULL ($(wc -l < "$FULL") lines) and $INDEX."
