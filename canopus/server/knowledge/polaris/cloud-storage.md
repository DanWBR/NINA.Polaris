# Storage push (NAS, SSH, mounted folder, cloud)

Polaris can copy saved images off the host by itself: to a NAS over
SMB, to another machine over SFTP, to a mounted folder, or to a cloud
account (Google Drive, OneDrive, Dropbox, Nextcloud/WebDAV, SFTP,
S3-compatible).

**The local copy always stays on the host.** The push is a copy, never
a move. Nothing is ever deleted at either end, so a failed or aborted
upload can cost you time but never data.

Settings -> Storage push.

## The four kinds

| Kind | What it speaks | Typical use |
|---|---|---|
| `smb` | SMB / CIFS | a NAS or a Windows share on the LAN |
| `sftp` | SSH file transfer | a Linux box or another Pi |
| `local` | a path on the host | a USB SSD, or an already mounted share |
| `rclone` | whatever rclone speaks | the cloud providers above |

One destination is active at a time. Changing the kind reconnects.

## How the push runs

Every frame that Polaris saves is queued, and a background worker
copies it, rebuilding the same folder tree at the destination. Two
lanes: images, and the heavier video/SER files, so a 4 GB capture
does not park the queue behind it.

- A file is written to a `.part` name and renamed when complete, so a
  half-uploaded frame is never mistaken for a finished one.
- Failures are retried; repeated failures trip a breaker so a dead NAS
  does not spin forever. **Retry failed** in the card resets it.
- **Backfill** walks the capture folder and queues whatever the
  destination does not already have, comparing sizes.
- `Link share` caps how much of the network link the push may use, so
  the browser sharing that link stays usable. It applies to the LAN
  kinds only; the cloud kind uses **Upload speed limit** instead (see
  below).

## The cloud kind uses rclone

Polaris does not implement Drive or OneDrive itself. It drives
[rclone](https://rclone.org), which already owns the provider
protocols, the OAuth sign in and the token refresh. Polaris runs one
`rclone copyto` per file and reads the progress off its JSON log.

**rclone is not bundled.** On a `.deb` install it is a recommended
package, so it usually comes along:

```
sudo apt install rclone          # Linux
winget install Rclone.Rclone     # Windows host
```

If it is missing, the Storage push card says so and lists every path
it looked in. Polaris also searches PATH, so a winget, choco or
Homebrew install is found wherever it landed.

Polaris keeps rclone's configuration in its own data directory,
`{DataDir}/rclone/rclone.conf`, mode 0600, and passes `--config` on
every invocation. It is written only through `rclone config`, never
by hand. **Cloud credentials never enter the Polaris profile** and are
never returned by the API.

### Setting up a remote on a headless host

The host has no browser, so the provider sign in happens on your own
computer.

1. Settings -> Storage push -> kind **Cloud** -> **Set up a remote**.
2. Pick the provider and give the remote a name (letters, numbers,
   dash, dot, underscore; no spaces and no colons).
3. On a computer that has a browser, install rclone and run the
   command the card shows, for example:

   ```
   rclone authorize "drive"
   ```

4. Sign in when the browser opens. rclone prints a block of text.
5. Paste that into the card and press **Create remote**.

**OneDrive is the exception.** `rclone authorize` alone does not
produce the `drive_id` and `drive_type` that OneDrive needs, so run
`rclone config` on your computer instead, complete it there, and paste
the whole `[name]` section out of your desktop `rclone.conf`. The
paste box accepts either shape.

WebDAV/Nextcloud, SFTP and S3 need no browser at all: they are a
plain form in the same panel.

The embedded terminal (SETTINGS -> Terminal) is the escape hatch: run
`rclone config --config <path>` there, with the config path the
Storage push card shows (by default
`~/.local/share/NINA.Polaris/profiles/rclone/rclone.conf`), and
Polaris picks up whatever you create. Always pass `--config`: under
systemd the service account's HOME is not the one you are typing in.

### Upload speed limit

The LAN kinds are paced by a duty cycle Polaris runs itself. rclone
owns its own transfer loop, so that control cannot reach it; the cloud
kind gets a hard cap instead (`--bwlimit`), chosen from a list.
Pick something below your actual upload speed so the session, the
preview and the guiding stay responsive while a night uploads.

## When things get uploaded

- **Each frame as it is saved**, the default, same as the LAN kinds.
- **When a run finishes**, if *Upload the session folder when a run
  finishes* is on. Everything the run touched goes up, including the
  files a live stack, a plate solve or a Studio step wrote without
  saving a new frame.
- **On demand**, from FILES: select exactly one folder and press
  **Send to cloud**. Only what the destination does not already have
  is queued.

A folder send must stay inside the capture folder, because the whole
point is mirroring that tree.

### Old rclone

Debian and Ubuntu package rclone 1.60, which is what most SBC images
carry. Polaris works with it and checks the version: `--partial-suffix`
only exists from 1.63, and an unknown flag makes rclone fail the whole
command, so on an older binary Polaris leaves the flag out. The only
thing lost is the partial-name guarantee. To get it, install a current
rclone from rclone.org instead of the distribution package.

## Known limits

- One rclone process per file. A very large backfill is therefore
  process-heavy; it buys exact per-file progress, retry and abort.
- Google Drive's shared client ID has a per-project quota. A heavy
  backfill can hit a 403 rate limit; slow it down or retry later.
- The end-of-session upload is scoped by modification time, so
  something else writing into the tree during a run can add a file or
  two. Nothing is deleted, so the cost is one extra upload.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Card says rclone is not installed | install the package; the card lists where it searched |
| "unknown remote" on the first upload | the remote name in the card does not match `rclone listremotes` |
| Test says the folder will be created | normal, the remote folder does not exist yet |
| Uploads stop, queue frozen | the breaker tripped; fix the destination and press Retry failed |
| A `.part` file left at the destination | an aborted upload; rclone writes to a partial name and renames on success, so the truncated copy never takes the real name. Delete it at the far end when it bothers you, Polaris never deletes anything on the remote |
| A truncated file under the real name | rclone older than 1.63, which has no partial-name support. The card says so when it sees one. The next send replaces the file, because the sizes differ |
| OneDrive remote authorises but uploads fail | created from `rclone authorize` alone, so it carries no drive id; recreate it from a full `rclone config` section |

## See also

- [FILES](files.md): the browser the folder send starts from
- [Relay](relay.md): reaching the host itself from outside
- [Network mode](network-mode.md): getting the host onto a network at
  all
