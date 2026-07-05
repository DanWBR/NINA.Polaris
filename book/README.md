# The Polaris Handbook

The single, organized reference for everything N.I.N.A. Polaris: usage and
internals, built as a [Quarto](https://quarto.org) book. It consolidates the
fragmented Markdown pages under `docs/` into one coherent narrative.

## Status

All chapters and appendices are consolidated: the preface, chapters
01 to 31, and appendices 90 to 93 are finished book prose. Each file
ends with a `<!-- consolidated from: ... -->` comment naming the
`docs/` sources it replaced. Where those sources contradicted the
code, the book follows the code and the trailing comment records the
reconciliation (ports 5000/5080 per the GX-10b defaults, APASS DR9,
mDNS `polaris-app-XXXX`, the relay `/_tunnel` path, among others).

Screenshots: 25 figures are placed from
`website/public/assets/screenshots/` (copied into `book/images/`).
Spots still lacking a matching capture keep a
`<!-- screenshot: ... -->` comment; grep for it to get the current
want-list (polar-alignment result, Bahtinov overlay, embedded PHD2
window, simulator settings, PREVIEW tab, ADV tri-pane, file browser,
Combine dialog, EDITOR).

No `<!-- TODO cite: ... -->` markers remain; every citation in the
72-entry bibliography is resolved. Before any print run, verify the
entries against ADS or the publisher, per the policy at the top of
`references.bib`.

## Building

Requires [Quarto](https://quarto.org/docs/get-started/) (on Windows:
`winget install Posit.Quarto`). For PDF output, also run
`quarto install tinytex` once (skip if you already have a LaTeX
distribution on PATH).

```sh
cd book
quarto render --to html   # book website in _build/
quarto render --to pdf    # print PDF in _build/
quarto preview            # live-reload preview while writing
```

## Citations

Every technical claim cites its source. Algorithms, file formats,
protocols, catalogs, and third-party software all get a proper reference;
software is cited like any other work (author, title, URL, access date).

- All entries live in `references.bib` (BibTeX). Add a DOI whenever one
  exists, and verify new entries against ADS
  (<https://ui.adsabs.harvard.edu>) or the publisher before print.
- Cite in text with `[@key]` (parenthetical) or `@key` (narrative), the
  Pandoc/Quarto syntax. The bibliography renders automatically into
  `99-references.qmd` in every output format.
- The citation style is Quarto's default (Chicago author-date). To switch
  (for example to an A&A or IEEE style), drop a `.csl` file in this
  directory and point `csl:` at it in `_quarto.yml`.

## Plain language

The reader is an astrophotographer, not a software developer. Every
technical term must be explained in plain language on its first use in
each chapter:

- Prefer rewriting the sentence so the term is not needed at all.
- Short gloss: parentheses, e.g. "WebAssembly (a technology that lets
  the browser run near-native-speed code)".
- Longer explanation: a footnote, Quarto inline syntax `^[...]`.
- Tool names get one clause saying what the tool is on first mention,
  e.g. "PHD2, the free autoguiding program most amateurs use".
- Astronomy terms get a short in-context line and a fuller entry in the
  glossary appendix.

Never condescending and never less precise: add the explanation, keep
the fact.

## Conventions

- One chapter per `.qmd` file, numbered by reading order; appendices are 9x.
- Chapter anchors are `{#sec-<slug>}` so cross-references survive renumbering.
- No em-dashes; use commas, colons, or semicolons.
- UI elements in **bold**, sidebar tabs in capitals (RIGS, STUDIO), paths
  and commands in `monospace`. The server machine is the "host", the
  browser device is the "client".
- When a chapter is consolidated, keep its "Sources to consolidate" list
  until the corresponding `docs/` pages are updated to point at the book.
