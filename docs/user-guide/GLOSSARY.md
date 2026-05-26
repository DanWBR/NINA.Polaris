# Glossary

Astrophotography terms used throughout the Polaris UI and this guide,
explained in 1-2 lines each. Backlinks from the feature pages point
here.

## A

**Alpaca**, A modern, HTTP-based protocol for astronomy devices
(ASCOM's REST replacement). Polaris speaks Alpaca natively; on
Windows you can also bridge any ASCOM driver through the
[ASCOM Remote Server](https://github.com/ASCOMInitiative/ASCOMRemote).

**Astrometric solution / plate solve**, Identifying the exact RA/Dec
that a frame is pointed at by matching stars to a catalog. Used to
re-center the mount when its pointing is off. Polaris uses ASTAP by
default and can fall back to PlateSolve3 / Astrometry.net online /
local.

**Autorun**, Polaris's name for the simple Sequence runner (vs. the
tree-based Advanced Sequencer).

## B

**Backspacing / back-focus**, The mechanical distance from the OTA's
flange to the camera sensor that the optical design expects.
Reducers/flatteners publish their required value (typically 55mm or
56mm for SCT/RC + camera combos). Polaris's Main Telescope card shows
the required value once you pick an OTA + accessory from the
catalogue.

**BGE (Background Extraction)**, Removing the gradient from a stacked
image (light pollution, moon glow). Polaris invokes
[GraXpert](https://www.graxpert.com/) for this.

**Bias**, A zero-exposure dark frame. Captures only the sensor's
read noise. Subtract from lights + flats to correct for read noise
pattern.

**Binning**, Combining adjacent pixels in hardware (e.g. 2×2 = four
pixels read as one). Reduces resolution but boosts SNR + framerate.

## C

**Calibration** (in PHD2 context), Teaching PHD2 how the mount moves
in response to pulse-guide commands. Done once per pointing region;
re-done after a meridian flip if the camera isn't oriented identically.
Polaris's Smart Calibrate button automates the step calculation.

**Calibration frames** (general), Bias + dark + flat frames used to
correct sensor artifacts in light frames. See [Studio](studio.md) for
generating masters.

**CCD_VIDEO_STREAM**, An INDI property that some cameras (ZWO, QHY,
gphoto DSLR) expose to flip into continuous BLOB streaming mode at
10-30 fps without per-frame `CCD_EXPOSURE` round-trips. Polaris's
camera stream auto-detects + uses it when available.

**Crowded field / cosmetic correction**, Removing hot/cold pixels from
a stack via sigma rejection.

## D

**Dark frame**, A long exposure with the shutter closed (or lens
covered). Captures thermal noise. Subtracted from lights to remove
hot pixels + dark current.

**Dec** (Declination), The celestial-coordinates equivalent of
latitude. Measured in degrees from −90° (south pole) to +90° (north).
Half of a (RA, Dec) coordinate pair.

**Dither**, Slightly shifting the mount between exposures so that
hot pixels + walking patterns don't align in the stack. Polaris uses
PHD2's `dither` JSON-RPC method, configurable per sequence.

**Drift**, The slow movement of a target across the sensor over time
due to mount tracking error + atmospheric refraction. Polaris's
[live-stacking auto-recenter](live-stacking.md) corrects this on a
schedule.

## E

**EAA (Electronically Assisted Astronomy)**, Watching a stacked
image build up live on screen during a session, rather than processing
offline later. Polaris's LIVE tab is purpose-built for this.

## F

**Filter offsets**, Per-filter focus position deltas (in focuser
steps) relative to a reference filter (usually L). Persisted per-rig.

**FITS**, The standard astronomy image format (header + raw pixel
array). 16-bit usually. Polaris reads and writes FITS by default.

**Flat frame**, An evenly-illuminated exposure used to correct vignetting
+ dust mote artifacts. Captured by pointing at a flat panel, twilight
sky, or T-shirt over the OTA aperture.

**FOV (Field of View)**, How much sky the camera + telescope combo
covers. Calculated from sensor size + focal length. Polaris shows
yours on the Main Telescope card.

## G

**Goto**, Slewing the mount to a specific (RA, Dec) target.

**GraXpert**, External tool for AI-based background extraction +
deconvolution + denoising. Polaris invokes the CLI.

**Guide camera**, A secondary camera (usually on a separate guidescope
or off-axis guider) used by PHD2 to track a star + send correction
pulses to the mount. Polaris doesn't manage it directly; PHD2 owns
it.

**Guide rate**, How fast the mount moves in response to PHD2's
pulse-guide commands, usually 0.5× sidereal (7.5"/sec).

## H

**HFR (Half-Flux Radius)**, A focus metric: the radius (in pixels)
within which half of a star's flux is contained. Smaller = sharper.
Polaris's auto-focus minimizes HFR via a V-curve sweep + parabola
fit; live stacking auto-refocus can trigger on HFR degradation.

## I

**INDI**, The Linux-first protocol for astronomy devices (cameras,
mounts, focusers, filter wheels). Polaris is INDI-native; `indiserver`
runs on your host on port 7624.

**ISO** (DSLR context), Sensor sensitivity. Polaris exposes ISO
selectors for vendor-driver DSLRs (Canon EDSDK, Nikon, Sony).

## L

**Laplacian variance**, A sharpness metric Polaris uses in its
planetary-stack pipeline to rank frames for lucky imaging. Higher = sharper.

**LST (Local Sidereal Time)**, The RA currently transiting your
meridian. Polaris calculates it to drive meridian-flip warnings.

**Lucky imaging**, Capturing thousands of short planetary exposures
then keeping only the sharpest X% for stacking. Polaris's VIDEO tab
implements this with Laplacian-variance ranking.

## M

**Meridian**, The imaginary north-south line passing overhead. A
GEM (German Equatorial Mount) needs to "flip" to the other side of
the pier when a target crosses it. Polaris's Meridian Flip workflow
automates pause-guiding / re-slew / re-solve / re-center / resume.

**MTF (Midtone Transfer Function)**, A non-linear stretch curve used
to display astro images. Polaris's WebGL renderer applies it on the
GPU at preview time without modifying the underlying FITS data.

## P

**PHD2**, The de-facto autoguiding tool. Polaris controls it via
JSON-RPC over TCP/4400.

**Pixel scale**, Arcseconds per pixel = (pixel size in µm × 206.265)
÷ focal length in mm. Defines your resolution + drives plate solving
+ guiding tuning.

**Plate solve**, See *astrometric solution*.

**Polaris (this app)**, Not to be confused with the star. Named after
the user, kind of.

## R

**RA (Right Ascension)**, The celestial-coordinates equivalent of
longitude, measured in hours (0..24) rather than degrees. Half of a
(RA, Dec) coordinate pair.

**Rig**, Polaris's term for a saved bundle of equipment selections +
per-setup defaults (cooler temp, focus step, focal length, PHD2 host,
algo preset, etc.). Switch rigs in one click via the dropdown in the
RIGS tab. Per-rig persistence means each setup keeps its own
configuration.

**ROI (Region of Interest)**, A subframe of the camera sensor for
faster frame rates (planetary imaging especially). Polaris's
`SetSubframeAsync` writes the INDI `CCD_FRAME` property.

## S

**SER**, A planetary-video format (header + raw uint8/uint16 frames
+ optional timestamp trailer). Used by AutoStakkert!, RegiStax, PIPP.
Polaris writes SER from the VIDEO tab.

**Siril**, External CLI for FITS preprocessing + stacking. Polaris
invokes it as an alternative to its built-in Studio pipeline.

**Sky Atlas**, A catalog of DSOs (galaxies, nebulae, clusters) browsable
in the SKY tab.

**Slew**, Moving the mount fast (vs. tracking, which compensates for
Earth's rotation). Polaris coordinates slew + plate solve + re-center
in the SKY tab.

**Stretch**, Converting linear pixel values to display-friendly
brightness via a non-linear curve (autostretch / manual sliders /
MTF). Required because human vision is logarithmic but FITS is linear.

## T

**Target**, In sequence context, a (name, RA, Dec) tuple the engine
slews to and shoots N frames of.

**Tracking**, The mount's continuous compensation for Earth's
rotation, usually at sidereal rate.

## V

**V-curve**, The HFR-vs-focuser-position plot that auto-focus uses
to find best focus. Sweeps N positions around current, fits a parabola
through valid samples, moves to the vertex.

## W

**WebSocket**, Polaris uses one (`/ws/status`) for the 1Hz status
broadcast and another (`/ws/image-stream`) for the live preview bitmap.
Both are HTTP-upgraded TCP connections.

**White balance**, Per-channel gain multipliers (R + B relative to G)
for color cameras. Polaris's VIDEO Capture tab exposes sliders when
the camera supports INDI's `WB_R` + `WB_B` properties (ZWO/QHY OSC).

## X

**xpra**, A Linux X11-forwarding-over-HTML5 tool. Polaris uses it to
embed PHD2's native GUI inside the GUIDE tab for operations PHD2's
JSON-RPC doesn't expose (Profile Wizard, Brain dialog, Guiding
Assistant).

**XISF**, PixInsight's image format. Polaris can write it as an
alternative to FITS.
