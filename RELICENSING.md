# Relicensing proposal: MPL-2.0 -> AGPL-3.0

Status: **PROPOSAL / DRAFT** on branch `license/agplv3-proposal`. Not merged.
The effective license of `master` is unchanged until this is reviewed (ideally
by a lawyer) and merged.

## Summary

N.I.N.A. Polaris is currently distributed under the Mozilla Public License
2.0 (MPL-2.0). This proposal relicenses the **work as a whole** to the GNU
Affero General Public License v3.0 (AGPL-3.0), while individual source files
derived from upstream N.I.N.A. remain available under MPL-2.0.

## Why this is permitted

1. Upstream N.I.N.A. (https://nighttime-imaging.eu/) is MPL-2.0. Its source
   files carry the standard MPL **Exhibit A** header and the copyright of
   Stefan Berg and the N.I.N.A. contributors. **None** carry the Exhibit B
   "Incompatible With Secondary Licenses" notice (verified across the
   upstream tree: Exhibit A present; Exhibit B absent).

2. MPL-2.0 section 1.12 names the **GNU AGPL v3.0** as a "Secondary License".

3. MPL-2.0 section 3.3 permits MPL-covered code that is *not* "Incompatible
   With Secondary Licenses" to be combined into a **Larger Work** distributed
   under such a Secondary License. The recipient may then use those MPL files
   under either the MPL-2.0 or the AGPL-3.0.

Therefore the combined work can be AGPL-3.0; the N.I.N.A.-derived files stay
MPL-2.0 (effectively dual MPL/AGPL by the recipient's option).

## Why AGPL (not GPL)

Polaris is a network-served web application (and ships a relay for remote
access). AGPL-3.0 section 13 closes the "SaaS loophole": anyone who runs a
modified Polaris as a network service must offer the modified source to its
users. GPL-3.0 would not cover that case. AGPL is also aligned with the
already-AGPL stellarium-web-engine bundled under `wwwroot/sky/`.

## Artifacts in this branch

- `LICENSE.txt` ............ replaced with the AGPL-3.0 text.
- `licenses/MPL-2.0.txt` ... the MPL-2.0 text, retained for the
                             N.I.N.A.-derived files.
- `NOTICE` ................. explains the AGPL-as-a-whole / MPL-per-file
                             relationship (MPL section 3.3) and credits
                             Stefan Berg / the N.I.N.A. contributors.
- `licenses/LINKING-EXCEPTION.txt` ... AGPL section 7 additional permission
                             for proprietary camera vendor SDKs and for
                             dynamically-loaded plugins.
- `scripts/apply-license-headers.ps1` and `.sh` ... idempotent helpers that
                             stamp file headers (see "Headers" below). NOT run
                             as part of this commit.

## Required actions before merge (checklist)

- [ ] **Legal review** of this whole change.
- [ ] **MPL section 3.4 compliance (do regardless of relicensing):** the
      `NINA.*.Portable` libraries contain code ported from N.I.N.A. but
      currently have no MPL header. Restore the MPL **Exhibit A** header +
      the N.I.N.A. copyright on every file that is actually derived from
      N.I.N.A. Removing/omitting those notices is an MPL violation today.
- [ ] **Audit provenance** file-by-file: confirm which `*.Portable` files are
      N.I.N.A.-derived (MPL header) vs. original to Polaris (AGPL header).
      The header script applies a *directory-level default* that must be
      verified, not trusted blindly.
- [ ] **GraXpert AI models (CC BY-NC-SA 4.0, NonCommercial):** keep them as an
      OPTIONAL separate download, NOT bundled inside the AGPL source tree /
      release artifacts. Confirm the build/installer does not embed them.
- [ ] **Camera SDKs:** confirm the linking exception text covers every vendor
      SDK actually shipped, and that vendor licenses permit redistribution of
      their binaries as packaged.
- [ ] Update `README.md` "License" section to describe AGPL-3.0 + the MPL
      relationship (a proposed edit is included on this branch).
- [ ] Update the in-app "About / Third-party licenses" view if present.
- [ ] Decide the license of the **relay server** (`NINA.Relay.*`) - same
      AGPL, or a separate decision.
- [ ] Announce: published MPL versions remain available under MPL; only new
      releases are AGPL. The change is effectively irreversible going forward.

## Headers

Two header templates are used:

- **AGPL header** -> files original to N.I.N.A. Polaris.
- **MPL Exhibit A header** (with the N.I.N.A. copyright) -> files derived from
  N.I.N.A.

`scripts/apply-license-headers.*` stamps headers idempotently (skips files
that already have one). Review its DERIVED vs ORIGINAL directory lists before
running; it does not, and cannot, decide provenance for you.
