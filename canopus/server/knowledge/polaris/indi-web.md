# INDI Drivers manager (embedded indi-web)

Polaris can embed the **indi-web** ([indiwebmanager](https://github.com/knro/indiwebmanager))
UI inside its own RIGS tab so you can start, stop, and configure
INDI drivers without ssh'ing into the host and editing
`indiserver` command lines by hand.

Unlike the embedded PHD2 GUI (which needs xpra to stream a desktop
window), `indi-web` is already a browser app, Polaris just
reverse-proxies it through `/indi-web/` and shows it in an iframe.
No extra display server, no extra streaming bandwidth.

## When you need this

Polaris's normal `IndiClient` connects to a running `indiserver` on
port 7624 and lists whatever drivers that server has already loaded.
It cannot, by itself, add or remove drivers, that requires either
restarting `indiserver` with new arguments or talking to its FIFO.
The embedded `indi-web` panel gives you:

- A checklist of every INDI driver installed on the host
- Profiles (groups of drivers you turn on together: "Mono SHO rig",
  "OSC travel rig", "Sim only")
- Start / stop / restart of `indiserver` from the browser
- Telescope simulator + utility drivers without command-line wizardry

Without it, every "I plugged in a new accessory and need its driver
loaded" trip means ssh + `pkill indiserver` + relaunching with the
new driver in the argv. With it, you tick a checkbox and click
Start.

## Requirements

| | |
|---|---|
| OS | Linux or macOS. Windows is unsupported, `indiserver` itself doesn't ship for Windows. |
| Python | 3.x with `pip` on PATH. |
| INDI core | Installed via `apt install indi-bin` (Debian/Ubuntu/Raspberry Pi OS) or your distro's equivalent, `indi-web` shells out to `indiserver`, it does not bundle it. |
| Port | 8624 (`indi-web`'s default). Bound to `127.0.0.1` only, Polaris proxies access through itself. |

## Install

One-line, on the Polaris host:

```bash
pip install indiweb
```

You don't need to start `indi-web` yourself, Polaris's
`IndiWebManagerService` detects the binary, manages the process,
and surfaces it in the RIGS tab.

If `pip install` lands in a virtual environment (recommended on
Raspberry Pi OS Bookworm+, where system-wide `pip install` is
blocked by PEP 668), point Polaris at the absolute path of the
`indi-web` binary in your `appsettings.json`. For a plain venv:

```json
{
  "IndiWeb": {
    "ExecutablePath": "/home/polaris/.venv/polaris/bin/indi-web",
    "AutoStart": true,
    "Port": 8624,
    "BindAddress": "127.0.0.1"
  }
}
```

For a pipenv-managed install (per the upstream README's
recommended path):

```bash
sudo apt install pipenv
cd ~ && mkdir indiweb && cd indiweb
pipenv --python=$(which python3)
pipenv install indiweb
# discover the venv path pipenv chose:
pipenv --venv
```

`pipenv --venv` prints something like
`/home/polaris/.local/share/virtualenvs/indiweb-AbCd1234`. The
binary is at `{that path}/bin/indi-web`. Plug it into
`IndiWeb:ExecutablePath` the same way:

```json
{
  "IndiWeb": {
    "ExecutablePath": "/home/polaris/.local/share/virtualenvs/indiweb-AbCd1234/bin/indi-web",
    "AutoStart": true
  }
}
```

The hash suffix changes if the Pipfile changes (rare); set once
and forget unless you `pipenv update` to a new release.

`BindAddress` should stay on loopback unless you really know what
you're doing, `indi-web` has no auth, so binding it to `0.0.0.0`
re-exposes driver control to anyone on the LAN.

## Workflow

1. Open the **RIGS** tab in Polaris.
2. Scroll to the **INDI Drivers** section near the bottom of the
   page (below Accessories).
3. The status pill shows the current state:
   - **● Running**, green, indi-web is up and the iframe is loaded
   - **Stopped**, installed but not currently running, click ▶ Start
   - **Not installed**, pip install hint shown inline
   - **OS not supported**, Windows banner with the reason
4. Click **▶ Start**. The iframe mounts after the TCP probe
   confirms indi-web is listening (typically 1-3 seconds).
5. Use the embedded UI to: pick a Profile (or create one), tick
   the drivers you want, click "Server" → "Start". indi-web now
   owns the `indiserver` process; Polaris's normal INDI client
   tabs (Mount, Camera, etc.) see the loaded drivers in their
   device dropdowns.
6. When you're done, click **■ Stop** in the Polaris control row
   to shut indi-web down. On a packaged host that is
   `systemctl stop polaris-indiweb.service`; elsewhere Polaris
   kills the child process tree it spawned.

## Coexistence with the built-in Simulator

Both `IndiWebManagerService` and the built-in
`SimulatorService` (Settings → Simulator) want to control
`indiserver`. They get along but you have to pick one owner per
session:

- **You're using indi-web for everything** (real hardware OR
  simulators) → leave SimulatorService disabled. Use indi-web's
  Profile dropdown to load the simulator drivers (`indi_simulator_ccd`,
  `indi_simulator_telescope`, etc.) when you want a dry-run.
- **You're using SimulatorService for dry-runs** → leave indi-web
  stopped. SimulatorService spawns its own indiserver via FIFO.

If both run at once they will race on the same FIFO and one of
them loses commands. A future Polaris version will route
SimulatorService's start/stop commands through indi-web's REST API
when indi-web is the active owner, but for now: pick one.

## Auto-start on boot

`appsettings.json` → `IndiWeb:AutoStart` = `true`. With auto-start
on, the service launches `indi-web` ~3 seconds after Polaris
itself comes up; the RIGS tab's INDI Drivers section shows the
running iframe immediately on first open.

Without auto-start (the default), `indi-web` only runs when you
click **▶ Start**.

## Troubleshooting

**Banner says "indi-web not detected" but I installed it.**
Polaris looks at the `PATH` of the user running Polaris. If you
installed with `pip install --user` and ran Polaris under a
different user (e.g. systemd as `polaris`), `~/.local/bin` may not
be in that user's PATH. Either:

- Install system-wide with `sudo pip install indiweb`
- Or set `IndiWeb:ExecutablePath` to the absolute path in
  `appsettings.json`

**Banner shows the binary path but status is "Not installed"
(detection runs, comes back empty).**
The user that runs Polaris cannot read/execute the path. With
systemd that user is whoever the unit's `User=` directive names
(often `polaris` or `root`), not your interactive login. Verify:

```bash
# pretend to be the Polaris user and try the binary directly:
sudo -u polaris /home/polaris/.local/share/virtualenvs/indiweb-XXXXXXXX/bin/indi-web --version
```

If that errors with "Permission denied" or "No such file or
directory", fix one of:

- Make the venv readable by the Polaris user (`chmod -R o+rx`
  on the venv tree if it's currently 700, or `chown -R polaris:polaris`
  if it should belong to that user)
- Move the Polaris systemd unit to run as the user who DOES own
  the venv (edit `User=` in `/etc/systemd/system/polaris.service`)

**Start failed with `ModuleNotFoundError: No module named 'cgi'`.**
Python 3.13 removed the `cgi` module from the standard library, but
indi-web's vendored `bottle.py` still does `import cgi` at the top.
Fix by installing the PyPI backport into the same venv:

```bash
sudo /opt/polaris-indiweb-venv/bin/pip install legacy-cgi
sudo systemctl restart polaris.service
```

Path is `/opt/polaris-indiweb-venv/` when installed via the Polaris
.deb, or `~/.local/share/virtualenvs/indiweb-XXXX/` for a pipenv
install. This bites every install on Pi OS images shipped after
late 2025 which moved to Python 3.13 as the default; the .deb
postinst installs `legacy-cgi` automatically, so this only
affects users who set up indi-web manually.

**Status flips to "Stopped" right after I click ▶ Start.**
The child process spawned but died before the TCP probe could
catch it. Check `journalctl -u polaris -f` (or wherever your log
lands), Polaris logs `Spawning indi-web: {path} {args}` followed
by either the listener-up message or an `indi-web exited
prematurely (code N)` line with N being the exit code.

Common reasons:

- **HOME not set in the systemd unit.** Bottle (the framework
  indi-web uses) reads config from `$HOME` on startup. Some
  systemd units run with an empty `$HOME` and indi-web exits
  immediately. Add `Environment=HOME=/home/polaris` to the
  `[Service]` block of the unit and `systemctl daemon-reload`.
- **Port 8624 already in use.** Something else (an old
  indi-web from a previous shell, a docker container) holds the
  port. `lsof -i:8624` to find it, kill it, restart Polaris.
- **Missing system libraries.** indi-web depends on libindi
  being installed. `apt list --installed | grep indi-bin`, if
  empty, `sudo apt install indi-bin`.

**Iframe shows "Bad Gateway"** (502).
The reverse proxy got an error talking to `127.0.0.1:8624`. Check
the Polaris log for the inner error. Usually means indi-web died
between the status probe and the iframe fetch, click **⟳** in
the Polaris control row to re-probe.

**Iframe shows the indi-web UI but driver list is empty.**
That's an indi-web ↔ indiserver problem, not a Polaris problem.
Check that `indi-bin` is installed on the host (`apt list --installed | grep indi-bin`).
indi-web finds drivers by scanning XML files under
`/usr/share/indi/`; if that directory is empty there's nothing
to list.

**Polaris's INDI tabs don't see drivers I started via indi-web.**
Polaris's `IndiClient` connects via TCP to `indiserver` on the
configured host:port (Settings → INDI). Check that the host:port
match what indi-web's "Server" page reports.

**How indi-web is owned, and why it survives a Polaris restart.**

On a host installed from the `.deb` the package ships
`polaris-indiweb.service` and `IndiWebManagerService` drives it
with `systemctl start|stop|restart`. That unit exists for one
reason: a process forked from Polaris inherits
`polaris.service`'s cgroup, systemd's default
`KillMode=control-group` kills the whole cgroup on stop, and a
restart is a stop plus a start. So `systemctl restart polaris`
used to take indi-web down, and with it `indiserver` and every
driver: the mount stopped tracking and the camera lost its
setpoint over a few seconds of downtime in the layer above.
`setsid` does not help, a cgroup is inherited across fork and a
new session is still the same cgroup. Only a separate unit is a
separate cgroup.

`GET /api/indi/web/status` reports `managedBy`, either
`"systemd"` or `"child-process"`, plus `systemdUnit`. On macOS,
in a container without systemd as PID 1, or from a source
checkout, there is no unit and Polaris forks indi-web as before.

The unit is NOT enabled at boot: whether indi-web starts is still
`IndiWeb:AutoStart`'s decision, which Polaris acts on once it is
up. Do not add a second unit of your own, both would try to bind
8624 and one loses with `EADDRINUSE`. To use an indi-web outside
the packaged venv, set `INDIWEB_BIN` (and `INDIWEB_PORT` /
`INDIWEB_HOST`, which must match `IndiWeb:Port` and
`IndiWeb:BindAddress`) in `/etc/default/polaris-indiweb`.

If the journal shows Polaris logging "systemctl start
polaris-indiweb.service failed", the PolicyKit grant in
`/etc/polkit-1/rules.d/50-polaris-indiweb.rules` is missing or
polkit has not reloaded. Polaris then falls back to a child
process, so the panel still works, but the drivers go down again
on the next restart.

## Restarting a wedged driver

Sometimes a single INDI driver stops responding mid-session - most
often a camera driver that stops delivering frames (a "dropped
BLOB"): the exposure fires but the image never arrives and the
capture eventually times out. A device *reconnect* does not fix
this, because the stuck process is the driver itself, not the
Polaris↔device link.

When indi-web is running, the **Driver watchdog** panel (top of the
INDI Drivers sub-tab) gives you two things:

- **Per-driver restart** - every running driver is listed with a
  `↻ Restart` button that bounces just that one driver on the
  indiserver (via indi-web's `/api/drivers/restart/<label>`),
  leaving the others untouched. Far less disruptive than restarting
  the whole indi-web or the server.
- **Auto-restart** (on by default) - after repeated capture
  timeouts for the same camera, Polaris resolves its driver and
  restarts it automatically, then tells you. It is rate-limited
  (a minimum gap between restarts and a cap per 30 min); once the
  cap is hit it stops and asks you to intervene (check cabling /
  power / USB), so a genuinely broken driver isn't bounced forever.
  It only ever restarts a driver - it never moves hardware.

Auto-restart only works when indi-web owns the server. If Polaris is
pointed at an external `indiserver` you started yourself, the
watchdog can't restart the driver; it just surfaces the problem so
you can restart the driver on your INDI host.

Tunables (in `appsettings`, all optional): `IndiWatchdog:Enabled`,
`WedgeThreshold`, `WedgeWindowSec`, `MinRestartIntervalSec`,
`MaxRestartsPerWindow`, `RestartWindowSec`.

## See also

- [Equipment setup](equipment.md), pre-`indi-web` workflow,
  still relevant when you'd rather edit `indiserver` args manually
- [Simulator mode](simulator-mode.md), built-in equipment
  simulator (see Coexistence section above for the rules)
- [PHD2 deep integration](phd2-gui-embedding.md), for the more
  complex xpra-hosted PHD2 GUI; this is the lighter-weight cousin
