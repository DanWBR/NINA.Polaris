# Remote terminal

A browser-based SSH terminal embedded in the **SETTINGS → Remote terminal**
section. Lets you restart a service, tail a log, or run any other shell
command on the Polaris host (or any other Linux box on your LAN) without
having to plug a screen + keyboard into the Raspberry Pi.

Great for the canonical "I'm in the field, my Pi is two metres away in the
dark, and I just need to `sudo systemctl restart indiserver`" moment.

## Enabling it

Off by default, the WebSocket endpoint `/ws/terminal` returns 403 unless
you opt in.

Edit `appsettings.json` (next to the Polaris binary in your publish folder
on Pi: `/opt/nina-polaris/appsettings.json`; on Windows in the publish dir
or the source tree if you're running from `dotnet run`):

```json
{
  "Logging": { "LogLevel": { "Default": "Information",
                             "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "Terminal": { "Enabled": true }
}
```

Restart Polaris. The section in SETTINGS now connects when you click
**Connect**.

## Pre-requisites on the SSH target

The terminal talks to a normal `sshd` over TCP 22. The target machine needs:

### Polaris is on a Raspberry Pi (host = `localhost`)

Pi OS ships with OpenSSH Server pre-installed. If it's disabled:

```bash
sudo systemctl enable --now ssh
```

### Polaris is on Windows (host = `localhost`)

OpenSSH Server is **not** installed by default on Windows. In an Admin
PowerShell:

```powershell
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType Automatic

# Allow the inbound TCP 22 if the install didn't add the firewall rule
New-NetFirewallRule -DisplayName "OpenSSH Server" `
    -Direction Inbound -Protocol TCP -LocalPort 22 `
    -Action Allow -Profile Private,Domain
```

### Polaris is somewhere else, you want to SSH into a separate Pi

The Pi already has `sshd` running on TCP 22. Just enter its IP / hostname
in the **Host** field. `polaris-app.local` works if mDNS is up.

## Using it

1. SETTINGS → scroll to **Remote terminal**.
2. **Host**, `localhost` for the Polaris host itself, or `192.168.x.x` /
   `name.local` for another machine.
3. **Port**, 22 (or whatever your `sshd` listens on).
4. **User**, your Linux / Windows username on the SSH target.
5. **Password**, your password. Wiped from the form the moment the auth
   frame is sent.
6. **Connect**.

A 80×24 terminal opens with a live PTY. Everything works: `vim`, `htop`,
`tmux`, `journalctl -f`, colours, scrollback.

Click **Disconnect** when done, or just close the tab. A 10-minute idle
window also closes the session server-side.

## Optimize the SBC (raspi-config / armbian-config)

When Polaris runs on a Raspberry Pi or an Armbian board, the **Remote
terminal** card shows an **Optimize this SBC** launcher with a button for
whichever config tool is installed:

- **⚙ raspi-config** on Raspberry Pi OS
- **⚙ armbian-config** on Armbian (Orange Pi, Radxa, etc.)

Both are detected automatically (`/usr/bin` or `/usr/sbin`); the button only
appears when the binary exists. Clicking it:

1. enables the remote terminal (one-time risk prompt) if it isn't already;
2. fills **Host** = `localhost`, **Port** = 22, and queues
   `sudo raspi-config` / `sudo armbian-config`;
3. you enter your **SBC login + password** and Connect — the config TUI starts
   automatically. `sudo` prompts for your password once.

Use it to overclock, set the GPU memory split, enable I2C/SPI/serial, expand
the filesystem, change locale/timezone, etc. Arrow keys, Enter and Esc all work
in the whiptail/ncurses menus.

**Root model:** the elevation is **your own `sudo`** inside your SSH session —
Polaris never gains passwordless root and adds no sudoers rule. The launcher is
just pre-typed input into your shell.

**Prerequisites:** the SSH server must be running on the board and your login
user must have `sudo` (the default `pi` / first Armbian user do). Same setup as
the rest of this page.

## Security model, read this

- Polaris never persists credentials. They live in memory for exactly the
  lifetime of the WebSocket and disappear on close.
- No auto-reconnect, each session prompts for credentials again.
- The WebSocket runs over plain HTTP unless you put Polaris behind HTTPS.
  On a LAN that's usually fine; over the internet it isn't. Use the Relay
  server (which terminates TLS via Let's Encrypt) or set up a reverse
  proxy in front of Polaris if you'll expose this beyond the LAN.
- `Terminal:Enabled = true` makes the endpoint exist. **Don't enable it on
  hosts you don't trust the LAN of.** Polaris doesn't add authentication
  to its own surface, the SSH credentials themselves are the only gate
  between the WebSocket and a shell.
- Bonus: if your `sshd` enforces public-key auth and the password the user
  enters doesn't match, the SSH connect fails the same way it would from
  a terminal. The "no password auth" config on the SSH side is honoured.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| **"WebSocket error. Is Terminal:Enabled=true on the server?"** | Server has `Terminal:Enabled = false` (default) | Edit `appsettings.json`, restart Polaris. |
| **"SSH connect failed: Unable to connect..."** | Target host has no `sshd`, or wrong port | See pre-requisites above. `Test-NetConnection host -Port 22` from a separate PowerShell to confirm. |
| **"SSH connect failed: Permission denied"** | Wrong password, or sshd is key-only | Either fix the password, or add password auth to `/etc/ssh/sshd_config` (`PasswordAuthentication yes` + restart). |
| Black box opens but nothing types back | SSH connected but the PTY didn't allocate | Try a different terminal type. Some embedded targets only support `vt100`; raise an issue with the host details and we'll add a setting. |
| Garbled output, weird boxes instead of borders | Font missing on the target | The xterm.js side uses xterm-256color. On Pi OS / Ubuntu / Debian it Just Works. On extra-stripped distros, set `TERM=xterm-256color` in the SSH user's `.bashrc`. |

## What this won't do

- Doesn't open a shell on the Polaris host directly without going through
  SSH. If you're locked out of SSH the terminal is also locked out.
- Doesn't run as `root` unless you `sudo` once you're in.
- Doesn't tunnel arbitrary TCP. It's a shell channel, not a SOCKS proxy.

For broader remote-management needs (file transfer, GUI app forwarding,
etc.) use the embedded PHD2 GUI panel (xpra-based) or a normal SSH client.
