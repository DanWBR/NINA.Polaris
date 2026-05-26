# NINA.Mount.SynScanWifi, Architecture

Direct-WiFi driver for **Sky-Watcher SynScan** mounts (AZ-GTi, EQ-GTi,
HEQ5 Pro / EQ6-R with the WiFi adapter, AllView, GTi mini-pier, ...).

Skips the INDI / ASCOM driver layer entirely, talks to the mount's
own UDP server on port 11880 using the SynScan App protocol.

This is the fallback path for users who don't want to run an INDI
server alongside Polaris (typical AZ-GTi user with a phone-only setup
adapting to a Polaris host).

## Layout

```
src/NINA.Mount.SynScanWifi/
  NINA.Mount.SynScanWifi.csproj
  SynScanUdpClient.cs            # UDP socket + send/recv pump
  SynScanCommandCodec.cs         # 8-byte command framing
  SynScanWifiTelescope.cs        # ITelescope impl
```

## The SynScan WiFi protocol

The mount listens on UDP/11880. Each command is an 8-byte packet:

- byte 0: command ID
- byte 1-2: optional parameters
- bytes 3-7: reserved / payload depending on command

Standard commands cover slew (J2000 + epoch-of-date variants), get/set
RA/Dec, tracking on/off, park, sync, get firmware version, get/set
guide rate, alignment state.

`SynScanCommandCodec` builds + parses those frames. `SynScanUdpClient`
holds the socket + a `BlockingCollection<Command>` for outgoing,
spins a recv loop, correlates replies to the matching command via
sequence number.

## `SynScanWifiTelescope` (the ITelescope impl)

Implements `NINA.Image.Portable.Interfaces.ITelescope`. Methods map
1:1 to SynScan commands:

- `ConnectAsync` → ping the mount, read firmware version
- `SlewToCoordinatesAsync(ra, dec)` → SYNC + GOTO commands
- `RightAscension` / `Declination` → polled every 500ms via `GET_POSITION`
- `Tracking` / `Slewing` getters
- `Park` / `Unpark`
- `IsSlewing` polled status

## How NINA.Polaris uses it

`EquipmentManager.SelectTelescope("synscan-wifi", ipAddress)`
instantiates this driver instead of `IndiTelescope`. Same `ITelescope`
contract above, so the rest of the app (sequencer, slew & center,
meridian flip) is unchanged.

The driver dropdown in the Mount card includes "SynScan WiFi" as an
option alongside INDI and Alpaca.

## See also

- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
- Sky-Watcher's SynScan App protocol docs (not redistributable; web
  search "SynScan WiFi commands" turns up reverse-engineered references)
