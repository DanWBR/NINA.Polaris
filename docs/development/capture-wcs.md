# Capture WCS

## Reference behaviour

The implementation was checked against KStars/Ekos and INDI source on
2026-10-16:

- [Ekos Align::setWCSEnabled](https://github.com/KDE/kstars/blob/master/kstars/ekos/align/align_devices.cpp#L631)
  enables the camera's `WCS_CONTROL` switch.
- [Ekos solverFinished](https://github.com/KDE/kstars/blob/master/kstars/ekos/align/align_solver.cpp#L813)
  sends the solved orientation to `CCD_ROTATION_VALUE`.
- [INDI CCD::addFITSKeywords](https://github.com/indilib/indi/blob/master/libs/indibase/indiccd.cpp#L2285)
  writes TAN WCS using the mount's J2000 position, camera rotation, pixel size,
  focal length and binning. It does not solve each capture.

Polaris writes this information at its common `ImageWriterService` save point,
so it works with camera backends independently of INDI snooping and includes
XISF output. No INDI properties or mount movement commands are changed.

## Geometry and scope

`PlateSolveOptions.CaptureContext` explicitly marks solves of live main-camera
frames. Imported-file, auxiliary-camera and polar-alignment solves do not set
it. The latter can use a different camera or an alignment configuration that
should not become capture calibration. A successful marked solve publishes an
immutable `CaptureWcsCalibration`; unmarked solves leave it untouched.

The calibration retains the solver's CD matrix, including optical parity.
Scalar-only solvers use the existing `WcsHeaders.FromSolveResult` convention.
A solve performed on a vertically flipped relay image is reflected back to
the original camera pixel axes. Solver adapters already undo their internal
downsampling; capture binning scales each CD column by the corresponding
ratio of output binning to solve-frame binning.

`CRVAL1/2` comes from the connected `ITelescope` in J2000 at save time, with
RA converted from hours to degrees. `CRPIX1/2` is the 1-based image centre,
`(size + 1) / 2`. No target coordinates or old solved centre are substituted
for a missing mount position. This is approximate capture WCS, not a fresh
astrometric solution of the saved exposure.

Reuse requires the same camera and telescope instances, rig ID, focal length,
pixel geometry, pier side and connected rotator angle. A cropped frame is
rejected by checking dimensions against sensor size and frame-reported
binning. Unknown sensor dimensions also prevent generating capture WCS.
Changes in manual rotation, flexure and alt-az field rotation cannot be inferred
from this calibration. A new solve is necessary after those changes.

The save operation wraps the pixel buffer and metadata with new image
properties rather than modifying the shared preview frame. Existing WCS wins.
Native RAW files remain verbatim, and calibration/auxiliary/stacked frames do
not acquire main-camera capture WCS.

## Verification

`CaptureWcsTests` exercises saved FITS headers and pixel data, binning, parity,
preview reflection, setup changes and XISF WCS preservation. FITS WCS numbers
are emitted as numeric cards, with no duplicate EQUINOX card. The existing
WCS, FITS, XISF and solver tests provide regression coverage.
