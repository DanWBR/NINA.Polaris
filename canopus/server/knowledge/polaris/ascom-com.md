# ASCOM (direct COM) — Windows

Polaris can talk to your ASCOM Platform drivers directly through
COM-interop, without going through ASCOM Remote Server or the
Alpaca Omni Simulator. The hop you'd otherwise pay (HTTP
localhost → COM) goes away, and you don't have to keep a separate
process running.

## When to pick this over Alpaca

| | ASCOM (COM, direct) | Alpaca (HTTP) |
|---|---|---|
| OS | Windows only | Any |
| Setup | Just install the ASCOM Platform | Install Platform + ASCOM Remote / Omni Sim |
| Latency | ~0 ms | ~1-3 ms localhost |
| Driver compat | Every ASCOM driver ever shipped | Only those exposed through the bridge |
| Service mode | Not when Polaris runs as SYSTEM | Works |

The short version: **on Windows, prefer ASCOM (COM, direct) unless
you specifically need Polaris to run as a Windows service.**

## What you need

1. **Windows**. The COM-interop path is Windows-only. Linux / macOS
   keep using INDI or Alpaca.
2. **ASCOM Platform 6.5 or 7.x**, from
   <https://ascom-standards.org/>. Free download.
3. **At least one ASCOM driver registered**. Most cameras, mounts,
   focusers, and filter wheels installed via their vendor installers
   land here automatically.

Polaris **does not** ship any ASCOM bits. If the Platform isn't
installed, the "ASCOM (COM, direct)" entry in the RIGS dropdown
shows up greyed out with a hint to install it.

## Connecting a device

1. RIGS tab → pick the device card (Camera / Mount).
2. **Driver** dropdown → "ASCOM (COM, direct)".
3. Click **🔍 Detect**. The dropdown fills with every ASCOM driver
   of that device type registered on the machine. The label is the
   description the driver author wrote into the registry (e.g.
   "ZWO ASI Camera", "iOptron CEM70", "Pegasus FocusCube3").
4. Pick the driver.
5. **(Optional)** Click **⚙ Setup** to open the driver's modal
   setup dialog — COM port pickers, mount-model pickers, filter
   naming, whatever the vendor exposed. Polaris waits for you to
   dismiss the form before continuing.
6. Toggle the connect switch ON. Polaris instantiates the driver,
   sets `Connected = true`, reads the metadata, and you're live.

## How it works under the hood

Each connected ASCOM device gets its own dedicated STA worker
thread. All property reads, property writes, and method
invocations against that driver go through that thread's serial
queue. This honours ASCOM's threading contract (drivers expect
STA apartment semantics) and keeps a slow operation on one device
(a 60 s telescope slew) from blocking another device on a
different thread (an autofocus loop on the focuser).

Cost: ~1 MB of stack + a kernel thread per connected device. A
typical 4-device rig (camera + mount + focuser + filter wheel)
uses 4 extra threads — negligible.

## Troubleshooting

### "ASCOM (COM, direct)" is missing or greyed out

- **Linux / macOS**: expected. Use INDI or Alpaca.
- **Windows but greyed out**: the ASCOM Platform isn't installed,
  or your user doesn't have read access to the
  `HKLM\SOFTWARE\ASCOM` registry hive. Install / reinstall the
  Platform and reopen RIGS.

### Detect button finds nothing

The driver vendor's installer didn't register the COM class for
your user. Re-run the installer (most need admin), or check
`HKLM\SOFTWARE\ASCOM\<Type> Drivers\` for the expected ProgID.

### Setup dialog "no interactive desktop" error

Polaris is running as a Windows service / under SYSTEM. The
SetupDialog needs an interactive desktop session to render the
form. Either:

- Start Polaris from your normal logged-in user account, OR
- Configure the driver once from another app that's interactive
  (the ASCOM Platform's Chooser dialog, the vendor's standalone
  tool), then come back to Polaris.

### Connect succeeds but the driver behaves oddly

ASCOM drivers vary in quality. Some advertise capabilities they
don't actually support, or throw `PropertyNotImplementedException`
on properties they're meant to implement. Polaris swallows the
common failures and substitutes neutral defaults (NaN for
temperature, false for cooler-on, etc.) — but if a critical method
fails, the toast surfaces the COM HRESULT verbatim so you can
report it to the driver vendor.

### 32-bit driver on 64-bit Polaris

Most modern ASCOM drivers ship as 64-bit (in-proc COM). A handful
of legacy drivers are 32-bit only and require ASCOM's COM
surrogate. Polaris detects these and lists them in the dropdown,
but you'll need to either:

- Use ASCOM Remote / Alpaca instead (works around the bitness gap),
  OR
- Ask the vendor for a 64-bit build.

## Comparison with the simulator
For development without hardware, use the **Equipment Simulator**
panel in Settings to spawn the ASCOM Omni Simulator. The Omni Sim
exposes every device type through Alpaca, which Polaris reaches
via its Alpaca path — no driver registration involved.
