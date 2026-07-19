# Common problems and quick diagnosis

A quick field guide to what a bad subframe is telling you. Look at the stars first:
their shape and color usually name the culprit.

## Elongated or trailed stars

- All stars streaked the same direction, everywhere: tracking or guiding failed
  (lost star, wind gust, bad polar alignment, cable snag, or a mount stall). Check the
  guide graph for that frame.
- Stars elongated more toward the corners than the center: optical, not tracking: coma or field curvature. A coma corrector or field flattener matched to your scope
  fixes it.
- One axis consistently worse: guiding problem in that axis (RA oscillation or DEC
  backlash/drift), or differential flexure between guide scope and main scope.

## Bloated or soft stars

Dew on the optics, poor focus, poor seeing, or being too low in the sky (high
airmass). Check for dew first (it creeps in and softens everything), confirm focus by
HFR, and prefer imaging targets higher up.

## Halos and reflections around bright stars

Often a filter reflection or an uncoated surface in the train, worse on very bright
stars. Some filters are more prone than others. A UV/IR cut filter tames refractor
star bloat. Internal reflections (a faint ghost offset from a bright star) come from
the optical train and may need a different filter or flocking.

## Gradients and uneven background

Light pollution, the Moon, or an airplane/satellite crossing leaves a brightness
gradient. Dither and shoot enough subs so the outliers reject, then use background
extraction in processing. Amp glow (a glow in one corner of the raw sub) is a sensor
trait removed by dark-frame calibration.

## Walking noise

A diagonal grain that "walks" across the stack in the direction of drift. It is
uncorrected fixed-pattern noise moving frame to frame because you did not dither.
Fix: always dither between subs; it randomizes the pattern so it averages out.

## Color problems (one-shot color)

- Strong green cast: normal from the Bayer matrix; remove green (SCNR) in processing.
- Magenta stars or halos: often channel misregistration or a debayer artifact; check
  the debayer pattern matches the camera.
- Wrong overall color: set a proper color balance, or use photometric color
  calibration against a star catalog for objective results.

## Nothing is showing up / very dark subs

Confirm the exposure is long enough that the sky background lifts off the left edge of
the histogram, the gain is set, the filter is not still on a dark/blocking slot, and
the scope cap is off. Under dark skies, faint targets simply need much more total
integration time; a single sub can look nearly black and still stack into a rich image.
