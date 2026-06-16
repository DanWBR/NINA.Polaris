# Software self-update (SBC / `.deb` installs)

When Polaris runs from the Linux `.deb` package (Raspberry Pi or any SBC/PC
installed via `apt`), it can update itself from
[GitHub Releases](https://github.com/DanWBR/NINA.Polaris/releases) with one
click — no SSH, no `apt` command, no sudo password.

> This feature only appears on a `.deb` install (the `/opt/polaris` layout
> with systemd). On Windows or a dev run there's no update badge.

---

## How it shows up

Polaris checks GitHub for a newer release on startup and once an hour. When a
release newer than the running version exists **for your host's architecture**
(`arm64`, `armhf`, `amd64`, …), a green **⬆ Update** badge appears in the top
status bar with the new version number.

![](screenshots/update-badge.png)

---

## Installing an update

Click the badge to open the update modal:

- **Release details** — version, publish date, the matched `.deb` asset and
  its size, full release notes, and a link to the release on GitHub.
- **Download & install** — downloads the architecture-matched
  `polaris_<version>_<arch>.deb` (the URL is resolved on the server from
  GitHub, never supplied by the browser) and installs it.

After you click install:

1. Polaris stages the `.deb` and starts a one-shot helper unit that installs
   it as root.
2. The package restart swaps in the new binary; the service comes back on the
   new version.
3. The browser polls until the server reports the new version, then reloads
   automatically. **Leave the page open** — it refreshes itself.

The whole thing takes anywhere from a few seconds to a couple of minutes on a
Pi (service restart + .NET warm-up).

---

## Why there's no password prompt

The Polaris service runs as the unprivileged `polaris` system user. The
install is authorized **passwordless** by a tightly-scoped PolicyKit rule
(`/etc/polkit-1/rules.d/50-polaris-update.rules`) that lets the `polaris` user
start **only** the `polaris-self-update.service` unit — the same passwordless
pattern used for the power, clock, and Wi-Fi actions. Your UI login is the
authorization; no separate sudo password is needed or stored.

The install runs in its own systemd unit (its own cgroup) so it survives the
`polaris.service` restart the package triggers.

---

## Requirements & first install

- Self-update works **from any version that already ships the updater** (the
  systemd unit + PolicyKit rule). The very first release with this feature
  still has to be installed manually:
  `sudo apt install ./polaris_<arch>.deb`. After that, updates are one click.
- The host needs internet access to reach GitHub and your apt mirror.

---

## Troubleshooting

- **"Not authorized to install the update."** — the PolicyKit rule is
  missing; reinstall the `.deb` to restore it.
- **Update seems stuck** — give it up to ~3 minutes; if the page never
  reloads, refresh manually. The install log is at `/tmp/polaris-update.log`.
- **No badge appears** — you're not on a `.deb` install, you're already on the
  latest version, or there's no internet to query GitHub. The check is cached
  for 30 minutes.

---

## Related

- [Installation](installation.md) · [Raspberry Pi setup](raspberry-pi-setup.md)
- [Remote terminal](remote-terminal.md) — manual service control if you ever
  need it.
