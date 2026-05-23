# Debug Polaris on a Raspberry Pi from Visual Studio

Developer-oriented guide. Four workflows, ranked by ergonomics:

- **[A. SSH remote debug (recommended)](#a-ssh-remote-debug-recommended)** —
  full step-debug from Visual Studio on Windows, breakpoints, watch,
  call-stack, just like local
- **[B. One-button deploy + restart via PowerShell script](#b-one-button-deploy--restart-via-powershell-script)** —
  `deploy\deploy-to-pi.ps1` does publish + scp + service restart in
  one go. No step-debug, but the fastest iteration loop without VS.
- **[C. Publish + SSH manual](#c-publish--ssh-manual)** — no debugger,
  just logs in stdout. Smoke-test only.
- **[D. Hot-reload via `dotnet watch`](#d-hot-reload-via-dotnet-watch)** —
  edit on Windows, auto-restart on Pi. No step-debug.

## A. SSH remote debug (recommended)

### 1. Prepare the Raspberry Pi (one-time)

```bash
ssh pi@<pi-ip>

# .NET 10 runtime (just runtime; SDK not needed to run published bits)
sudo apt update
sudo apt install -y libicu-dev libssl-dev curl libfontconfig1
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin \
    --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet
sudo ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
dotnet --info     # should show "Microsoft.AspNetCore.App 10.x.x"

# vsdbg — the debugger Visual Studio connects to over SSH
curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l ~/vsdbg

# Deploy folder
mkdir -p ~/polaris
```

`libfontconfig1` is required by SkiaSharp (image encoding). INDI server
is a separate install — see [installation.md](installation.md).

Confirm 64-bit OS: `uname -m` should print `aarch64`. 32-bit
(`armhf`) won't work — vsdbg ARM64 expects 64-bit kernel.

### 2. Configure Visual Studio on Windows

**a. Add the SSH connection**

`Tools → Options → Cross Platform → Connection Manager → Add`

- Host: Pi's IP
- Port: 22
- User: `pi` (or whatever user you SSH as)
- Auth: **key file** (generate with `ssh-keygen` on Windows + copy the
  public key with `ssh-copy-id pi@<pi-ip>` from WSL/Git-Bash)
- Test connection → Save

**b. Add the SSH debug profile**

Open `NINA.Polaris.slnx`. Right-click the `NINA.Polaris` project →
**Properties → Debug → "Open debug launch profiles UI" → Add → SSH**

Fill in:

| Field | Value |
|---|---|
| Hostname | (pick the connection from step a) |
| Project path on target machine | `/home/pi/polaris` |
| Executable | `dotnet` |
| Command line arguments | `NINA.Polaris.dll` |
| Working directory | `/home/pi/polaris` |
| Deploy on debug | ✓ checked |
| Environment variables | `ASPNETCORE_URLS=http://0.0.0.0:5000` |

**c. Target ARM64 in the csproj**

`NINA.Polaris.csproj` needs to publish for `linux-arm64`. Add (or
verify) inside the main `<PropertyGroup>`:

```xml
<RuntimeIdentifier>linux-arm64</RuntimeIdentifier>
<SelfContained>false</SelfContained>
```

`SelfContained=false` is important — we installed the runtime on the
Pi in step 1, no need to ship it on every deploy.

**d. Exclude Windows-only camera SDKs from the Linux build**

`NINA.Camera.CanonEdsdk` and `NINA.Camera.NikonSdk` target
`net10.0-windows` and won't compile for Linux. Wrap their
`ProjectReference` in a Linux condition:

```xml
<ItemGroup Condition="!$(RuntimeIdentifier.StartsWith('linux'))">
  <ProjectReference Include="..\NINA.Camera.CanonEdsdk\NINA.Camera.CanonEdsdk.csproj" />
  <ProjectReference Include="..\NINA.Camera.NikonSdk\NINA.Camera.NikonSdk.csproj" />
</ItemGroup>
```

`NINA.Camera.SonySdk` is plain `net10.0` (Sony ships Linux binaries) —
leave it alone.

### 3. Hit F5

Visual Studio does, in order:

1. `dotnet publish -r linux-arm64` locally
2. SCP the publish output to `/home/pi/polaris/` (delta-only after the
   first deploy)
3. SSH executes `dotnet NINA.Polaris.dll` with `vsdbg` attached
4. Breakpoints, watch, immediate window, call stack — all work as if
   the process were local

Open the browser to `http://<pi-ip>:5000` to drive the UI.

### Gotchas

- **First publish is slow** (~2 minutes) — copies the publish output
  + first-time NuGet cache. Subsequent deploys send only deltas.
- **CPU overhead with debugger attached**: Pi 4 is borderline for live
  stacking + debugger active. Pi 5 is much more comfortable for
  step-debug sessions.
- **`localhost` bind doesn't work**: by default the host's
  `appsettings.json` binds `localhost:5000`. The env var
  `ASPNETCORE_URLS=http://0.0.0.0:5000` in the debug profile
  overrides this. Without it you can't reach the server from another
  machine.
- **vsdbg path mismatch**: if VS complains it can't find vsdbg, set
  the debugger path explicitly under `Debug → Debug launch profiles
  → SSH → "Pre-launch command"` to point at `~/vsdbg/vsdbg`.

## B. One-button deploy + restart via PowerShell script

For the "edit something, see it on the Pi, fix it, repeat" loop
without spinning up the VS SSH machinery. Ships in `deploy/deploy-to-pi.ps1`.

### One-time setup on the Pi

1. **SSH key auth** (no passwords). From Windows:
   ```powershell
   ssh-keygen           # if you don't already have one
   ssh-copy-id pi@polaris.local     # WSL/Git-Bash, OR copy ~/.ssh/id_rsa.pub
                                    # into pi:~/.ssh/authorized_keys by hand
   ssh pi@polaris.local exit        # confirm it works without prompting
   ```

2. **.NET 10 ASP.NET runtime + libfontconfig1** on the Pi — same as
   section A step 1 above.

3. **(Optional but recommended)** A systemd unit so the deploy
   script can `systemctl restart` it cleanly. Easiest is a
   user-mode unit at `~/.config/systemd/user/polaris.service`:

   ```ini
   [Unit]
   Description=N.I.N.A. Polaris

   [Service]
   WorkingDirectory=%h/polaris
   ExecStart=/usr/local/bin/dotnet NINA.Polaris.dll
   Environment=ASPNETCORE_URLS=http://0.0.0.0:5000
   Restart=on-failure
   RestartSec=5

   [Install]
   WantedBy=default.target
   ```

   Then:
   ```bash
   systemctl --user daemon-reload
   systemctl --user enable polaris
   loginctl enable-linger pi    # so the user service starts at boot
                                # without you having to log in
   ```

   The deploy script auto-detects this unit and uses `systemctl
   --user restart polaris`. If the unit isn't present it falls back
   to `pkill + nohup`, which is fine for ad-hoc testing.

### Use

```powershell
# Pi 2/3 (32-bit Raspbian, ARMv7)
.\deploy\deploy-to-pi.ps1

# Pi 4/5 (64-bit Pi OS / Ubuntu)
.\deploy\deploy-to-pi.ps1 -Rid linux-arm64

# Explicit IP
.\deploy\deploy-to-pi.ps1 -PiHost 192.168.1.50

# Different user / path
.\deploy\deploy-to-pi.ps1 -PiUser dan -RemotePath /srv/polaris

# Just copy, don't bounce the service
.\deploy\deploy-to-pi.ps1 -NoRestart

# Just bounce, don't re-copy
.\deploy\deploy-to-pi.ps1 -NoCopy

# Debug config (bigger binaries, faster build)
.\deploy\deploy-to-pi.ps1 -Debug
```

End to end: `dotnet publish -r {rid}` → `scp -Cpr` → `ssh
'systemctl restart polaris OR pkill+nohup'`. First publish takes
~30s; subsequent deploys are mostly the scp (~5-15s depending on
LAN speed + Pi 2 slow flash) + a 1-second restart.

### Pi 2 Model B caveats

The Pi 2 (ARMv7, 900 MHz quad-core Cortex-A7, 1 GB RAM,
VideoCore IV GPU) is the slowest target Polaris supports. It works
for connection + basic capture tests, but:

- **RID**: must be `linux-arm` (32-bit), not `linux-arm64`. Confirm
  with `uname -m` → `armv7l`.
- **RAM**: 1 GB is tight. Polaris idle ≈ 120-180 MB; with live
  stacking + PHD2 + ASTAP simultaneously it can swap. Disable
  features you're not testing.
- **WebGL2**: Chromium on Raspbian 32-bit doesn't support WebGL2
  → live preview falls back to server-side JPEG (extra CPU on
  the Pi). Toggle "Force JPEG mode" in Settings to make it
  explicit.
- **CPU%**: anything image-heavy (stretch, debayer, star detect)
  pegs all 4 cores. Browser-side will be sluggish over the LAN.
- **For real work**: upgrade to a Pi 4 (4GB+) or Pi 5. Pi 2 is
  useful as a low-power smoke-test target.

## C. Publish + SSH manual

Skip the VS SSH plumbing. Use the publish scripts in `deploy/`:

```powershell
# On Windows
cd C:\Users\danie\source\repos\DanWBR\nina-polaris\deploy
# Use Git-Bash or WSL to run the .sh; or there's a .ps1 equivalent
bash publish-linux-arm64.sh

# scp to the Pi
scp -r ..\src\NINA.Polaris\bin\Release\net10.0\linux-arm64\publish\* pi@<pi-ip>:~/polaris/

# Run on the Pi
ssh pi@<pi-ip>
cd ~/polaris
ASPNETCORE_URLS=http://0.0.0.0:5000 ./NINA.Polaris
```

No step-debug — just logs in stdout. Useful for "did my fix actually
launch on the Pi" without spinning up the VS deploy machinery. Bad
choice if you're hunting an actual bug — use **A**. Bad choice if
you want a repeatable loop — use **B**.

## D. Hot-reload via `dotnet watch`

For tight iteration (edit on Windows, file syncs, Pi auto-restarts):

```bash
# On the Pi — needs the SDK, not just runtime
sudo apt install -y dotnet-sdk-10.0    # bigger install than the runtime
cd ~/polaris/src       # location of the source (see below for sync)
DOTNET_USE_POLLING_FILE_WATCHER=1 \
    dotnet watch run --project NINA.Polaris
```

To get the source onto the Pi without committing+pulling on every
save, use **SSHFS** from WSL to mount a Windows folder on the Pi:

```bash
# On the Pi
sudo apt install -y sshfs
mkdir -p ~/polaris-src
sshfs danie@<windows-ip>:/c/Users/danie/source/repos/DanWBR/nina-polaris \
    ~/polaris-src
```

Now `dotnet watch` running on the Pi sees Windows file changes
through the SSHFS mount + restarts the host on save.

Trade-offs:
- No step-debug (use `Console.WriteLine` / `ILogger` + `journalctl -f`)
- SSHFS adds latency to every file read — first build after mount
  takes 2-3× longer
- SDK on Pi adds ~500 MB
- File-watcher needs polling (`DOTNET_USE_POLLING_FILE_WATCHER=1`)
  because inotify doesn't propagate through SSHFS

Best for "tweaking CSS + JS without bouncing the server every time."
For C# changes, **A** is more pleasant.

## See also

- [Installation](installation.md) — getting Polaris running on the Pi
  for actual use (vs developer setup)
- [CONTRIBUTING.md](../../CONTRIBUTING.md) — overall dev workflow
- [ARCHITECTURE.md](../../ARCHITECTURE.md) — service layout +
  cross-project map
