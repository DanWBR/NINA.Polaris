# FILES tab (file explorer)

Server-side file manager. Browse the filesystem of the Polaris host,
preview FITS/XISF/text, download (single or as ZIP), set the Studio
root, do basic file operations.

## Why it exists

Without this you'd be stuck on `scp` / SMB / SSH to get files off the
Pi or move things around. With it you do everything from the browser.

## Layout

- **Drive picker** (left) — Windows: lettered drives; Linux: `/`,
  `/home`, `/mnt`, `/media`, `~`
- **Path crumbs** (top center) — clickable per-segment navigation
- **Toolbar**: New folder, Upload, Download, Cut, Copy, Paste, Rename,
  Delete, ⭐ Set as Studio root
- **Listing**: checkbox-select rows with name / size / modified /
  type columns; double-click folder = enter; double-click file =
  preview
- **Selection bar** (bottom) — "2 files · 124 MB" + current Studio
  root indicator

## Preview

- **FITS / XISF / TIFF / PNG / JPG** — opens in OpenSeadragon viewer
  (same component as STUDIO single-frame view), with auto-stretch for
  FITS
- **TXT / LOG / JSON / MD** — modal showing the first 32 KB of text

## Multi-download as ZIP

Select N files + Download → server streams a single ZIP straight to
the browser (no full ZIP in memory; supports hundreds of FITS without
blowing host RAM).

## Mutations (cut / copy / paste / delete / rename)

Standard semantics:

- Cut + Paste = move (handles cross-volume by copy + delete)
- Copy + Paste = duplicate
- Delete = `confirm()` prompt + server log line (always logged for
  destructive ops)
- Rename = inline edit on the row
- Cross-volume move: silent toast "moved across volumes — copied then
  deleted source"

## Set as Studio root

Navigate to a folder + click ⭐ → `profile.ImageOutputDir` updates +
the STUDIO tab re-indexes against the new root. This is the canonical
way to switch storage targets between sessions.

## Security model

Polaris assumes a trusted LAN. The FILES tab exposes the **entire
filesystem** of the host (within the user account running the server).
A blocklist covers obvious traps (`/proc`, `/sys`, `/dev/shm`,
`/etc/shadow`, `~/.ssh`, registry hives on Windows), and destructive
ops require double confirmation, but the surface is wide.

**Don't expose Polaris directly to the internet** without the Relay
server (which has tokens + TLS + per-tenant rate limits). See
[Relay guide](relay.md).

## Common pitfalls

**Permission denied on `/etc/...`** — blocklist. By design.

**Downloads of huge ZIPs time out** — your reverse proxy (if any)
needs a long read timeout. Direct LAN access via port 5000 has no
issue.

**Studio rescan doesn't see new files** — make sure you set the
Studio root deep enough (above `lights/`, not inside one specific
target).

## See also

- [Relay](relay.md) — remote access with auth
- [STUDIO](studio.md) — the consumer of `ImageOutputDir`
