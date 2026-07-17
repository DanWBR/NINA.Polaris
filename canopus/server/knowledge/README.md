# Assistant knowledge base

Markdown docs the assistant retrieves from (the `search_knowledge` tool →
`knowledge.py` BM25 index). This is the RAG corpus; it is not Polaris API state.
Two layers, both indexed together:

- **This folder (top level)** — hand-written, vendor-neutral general
  astrophotography notes (focusing, guiding, exposure, filters, polar
  alignment, calibration frames, planning, common problems).
- **`polaris/`** — a mirror of the full N.I.N.A. Polaris user guide (every tab,
  workflow and setting), so the assistant can answer product/navigation
  questions and orient confused users. **Do not hand-edit these** — they are
  generated. Re-sync from the docs with:
  `python scripts/sync-polaris-docs.py` (copies `nina-polaris/docs/user-guide`
  here; the guide's `README.md` lands as `polaris/index.md`). The synced files
  are committed so the deployed cloud has them without the nina-polaris checkout.

## How it works

- Each `.md` file is split into passages at `#`/`##`/`###` headings.
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
