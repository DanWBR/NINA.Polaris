# Assistant knowledge base

Markdown docs the assistant retrieves from (the `search_knowledge` tool →
`knowledge.py` BM25 index). This is the RAG corpus; it is not Polaris API state.
Two layers, both indexed together:

- **This folder (top level)**: hand-written, vendor-neutral general
  astrophotography notes (focusing, guiding, exposure, filters, polar
  alignment, calibration frames, planning, common problems).
- **`polaris/`**: the Polaris Handbook (`book/*.qmd`), one file per chapter,
  so the assistant can answer product and navigation questions and orient
  confused users. **Do not hand-edit these**, because they are generated.
  When the book changes, regenerate them from the repository root with
  `python scripts/sync-canopus-knowledge.py`. The script removes the Quarto
  syntax, keeps footnotes inline in parentheses, turns cross references into
  section names, and prefixes every section title with its chapter
  ("Autoguiding / Calibration"). The generated files are committed, so the SBC
  build and the cloud tier have them without a checkout of the book. The cloud
  tier runs the same script with `--dest` pointing at its own corpus.

## How it works

- Each `.md` file is split into passages at `#`/`##`/`###` headings. A `#`
  line inside a fenced code block is a comment, not a heading.
- A passage longer than 800 characters is split into chunks at paragraph
  boundaries, and every chunk keeps its section title, so a search result is
  never cut short.
- `knowledge.py` builds a from-scratch BM25 index over those passages (no external
  deps) and returns the top matches for a query.
- The agent exposes it as a local tool; the model calls it and grounds its answer in
  the returned passages.

## Editing

- Keep passages self-contained under clear headings (headings are weighted in
  ranking, so make them descriptive).
- Write original, vendor-neutral prose. Prefer principles and rules of thumb over
  brand- or number-specific claims that age badly.
- Add a new topic by dropping a new `.md` here; it is indexed automatically at
  startup. No code change needed.
- Later upgrade path: swap/augment the BM25 ranker in `knowledge.py` with embeddings
  while keeping the same `search()` interface and the same docs.
