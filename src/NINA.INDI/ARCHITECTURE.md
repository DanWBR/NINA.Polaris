# NINA.INDI, Architecture

INDI (Instrument Neutral Distributed Interface) protocol client +
device wrappers. Standalone library, depends on nothing in the
Polaris solution except `NINA.Image.Portable` (for `IImageData`
returned from `IndiCamera.CaptureAsync`).

INDI is the de-facto Linux/macOS driver framework for astronomy
equipment (ZWO, QHY, EQMod, gphoto-DSLRs, etc.). It speaks XML over a
TCP socket (default port 7624). This project implements the client
side of that protocol.

## Layout

```
src/NINA.INDI/
  Protocol/
    IndiProperty.cs          # vector/element model (numbers, switches,
                             # texts, lights, blobs)
    IndiXmlParser.cs         # streaming XML parser tied to socket recv
    IndiXmlWriter.cs         # builds newXXXVector commands to send
  Client/
    IndiConnection.cs        # raw TCP socket + send/recv loops
    IndiClient.cs            # high-level: enumerate devices, get/set
                             # properties, subscribe to BLOB events
    IndiBlobReceiver.cs      # BLOB framing (binary chunks delivered
                             # inline in the XML stream)
  Devices/
    IndiCamera.cs            # CCD_EXPOSURE, CCD_FRAME, CCD_VIDEO_STREAM,
                             # bayer pattern detection, cooler, gain, ISO
    IndiTelescope.cs         # EQUATORIAL_EOD_COORD, ON_COORD_SET (slew/
                             # sync/track), TELESCOPE_PARK, side-of-pier
    IndiFocuser.cs           # ABS_FOCUS_POSITION, FOCUS_TEMPERATURE
    IndiFilterWheel.cs       # FILTER_SLOT + FILTER_NAME
    IndiRotator.cs           # ABS_ROTATOR_ANGLE, REVERSE
    IndiFlatDevice.cs        # FLAT_LIGHT, brightness
    IndiDome.cs              # azimuth, shutter, slave
    IndiWeather.cs           # WEATHER_PARAMETERS (read-only)
    IndiGuider.cs            # PHD2-side guider devices (legacy; PHD2
                             # connection is via JSON-RPC in NINA.Polaris)
```

## Protocol layer

INDI is property-oriented: every device exposes named **property
vectors** (groups of elements). Each vector is one of:

- **Number**: e.g. `CCD_EXPOSURE.CCD_EXPOSURE_VALUE = 120.0`
- **Switch**: e.g. `CONNECTION.CONNECT = On / CONNECTION.DISCONNECT = Off`
- **Text**: e.g. `DEVICE_PORT.PORT = "/dev/ttyUSB0"`
- **Light**: read-only status indicator
- **BLOB**: binary data (FITS frames, debug images)

`IndiXmlParser` reads the socket as a stream of `<defXXXVector>`,
`<setXXXVector>`, `<message>`, and `<delProperty>` elements. It
materializes them into `IndiProperty` instances on a per-device,
per-property-name dictionary held by `IndiClient`.

`IndiXmlWriter` builds outbound `<newXXXVector>` elements with the
desired element values. The server applies them + emits a confirming
`<setXXXVector>` echo.

## Client layer

`IndiConnection` is the raw transport: a TCP socket + two background
tasks (one for recv, one for send queue). It surfaces:

- `Task ConnectAsync(string host, int port)`
- `event Action<XElement> ElementReceived`
- `Task SendAsync(XElement element)`

`IndiClient` sits on top:

- Maintains `Dictionary<DeviceName, Dictionary<PropertyName, IndiProperty>>`
- Subscribes to `ElementReceived` and updates that dictionary
- Exposes typed getters: `GetNumber(device, vector, element)`,
  `GetSwitch(...)`, etc.
- Exposes typed setters: `SetNumberAsync(...)` (builds
  `newNumberVector` and sends)
- `EnumerateDevices()` returns the device-name list
- Subscribes BLOB events to `IndiBlobReceiver` which reassembles binary
  chunks into byte arrays + raises `BlobReceived(device, propName,
  format, data)`

## Device wrappers

Each `Indi<Device>` class is a thin object-oriented facade over the
property dictionary, tailored for one device kind. They:

- Take `IndiClient` + a `string deviceName` in the constructor
- Cache subscriptions so the wrapper's `IsConnected`, `Temperature`,
  `Position`, etc. properties read from the client's dictionary
  without parsing every time
- Expose action methods (`ConnectAsync`, `CaptureAsync(seconds)`,
  `SlewToCoordinatesAsync(ra, dec)`, ...)
- Raise C# events for state changes that consumers care about
  (`CoordinatesChanged`, `BlobReceived`, ...)

`IndiCamera` is the most complex, it implements `NINA.Image.Portable.
Interfaces.ICamera`, deals with bayer detection, optionally starts/stops
`CCD_VIDEO_STREAM` for the planetary VIDEO tab, and converts the
incoming FITS BLOB into a `BaseImageData` via `FITSReader` from
`NINA.Image.Portable`.

## What this project doesn't do

- It doesn't know about Polaris's profile model, equipment manager,
  or any UI. Pure protocol library.
- It doesn't launch `indiserver`, that's an external prerequisite
  the user runs separately (or via `indiwebmanager`).
- It doesn't speak Alpaca / ASCOM, those live in
  `src/NINA.Polaris/Services/Alpaca/`.

## How NINA.Polaris uses it

`EquipmentManager` in NINA.Polaris owns a single `IndiClient`
instance. When a rig selects an INDI device (e.g. "ZWO ASI2600MC
Pro" as Camera), `EquipmentManager` instantiates the matching
`IndiCamera` wrapper bound to that device name and exposes it through
its typed property (`EquipmentManager.Camera`).

## Adding a new device kind

The existing 9 device wrappers cover virtually all astronomy
equipment. If you need a new one (e.g. "safety monitor"):

1. Read the INDI spec for that device type to know the standard
   property vector names
2. Create `Devices/IndiSafety.cs` with the wrapper pattern (constructor
   + property getters + action methods)
3. Wire it into `EquipmentManager` in NINA.Polaris
4. Add the endpoint group + UI card following the patterns in
   [CONTRIBUTING.md](../../CONTRIBUTING.md)

## See also

- [INDI library docs](https://www.indilib.org/develop/developer-manual/101-protocol.html)
 , the protocol spec
- [Root ARCHITECTURE.md](../../ARCHITECTURE.md), how this plugs into
  the rest of Polaris
