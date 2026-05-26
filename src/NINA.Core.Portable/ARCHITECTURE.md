# NINA.Core.Portable, Architecture

Smallest project in the solution. Holds **shared primitives** every
other library and the host depend on: enums, simple value types,
INotifyPropertyChanged base, the mediator interface, a few utility
helpers.

Pure code, no IO, no third-party deps. Lowest layer, everything
above (Image, INDI, Headless) depends on this; this depends on
nothing in the solution.

## Layout

```
src/NINA.Core.Portable/
  Enum/                            # shared enumerations
    BayerPatternEnum.cs            # RGGB / GRBG / BGGR / GBRG / Mono
    CameraStates.cs                # Idle / Exposing / Reading / Download
    DeviceTypeEnum.cs              # Camera / Mount / Focuser / ...
    FileTypeEnum.cs                # FITS / XISF / TIFF / PNG / JPEG / SER
    GuideDirections.cs             # North / South / East / West
    PierSide.cs                    # East / West / Unknown
    AlignmentMode.cs               # AltAz / Polar / GEM
    SensorType.cs                  # Monochrome / Color / RGGB / ...
    TelescopeAxes.cs               # Primary / Secondary / Tertiary
  Interfaces/
    IMediator.cs                   # tiny event-aggregator contract
  Model/
    ApplicationStatus.cs           # global "I'm doing X" message
    Equipment/                     # shared equipment value types
  Utility/
    BaseINPC.cs                    # INotifyPropertyChanged base class
    CoreUtil.cs                    # small helpers (clamp, format)
    Logger.cs                      # legacy logger shim (most of the
                                   # host uses ILogger<T> instead)
```

## Why split this out

These types live in the lowest project so the camera SDK wrappers
(`NINA.Camera.*`), the protocol library (`NINA.INDI`), and the image
library (`NINA.Image.Portable`) can all reference them without pulling
in heavier dependencies.

If you find yourself adding an enum that's used in two or more
projects, it belongs here.

## Conventions

- **Pure value types**: enums, simple records, immutable structs.
  No services. No DI.
- **No `System.Drawing`**, **no `System.IO.File`**, keep it truly
  portable.
- **`BaseINPC`**: legacy helper from upstream NINA. New code in
  Headless prefers immutable record snapshots over INPC for state,
  but this base class is still used by some equipment view-model-ish
  types passed between layers.

## See also

- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
