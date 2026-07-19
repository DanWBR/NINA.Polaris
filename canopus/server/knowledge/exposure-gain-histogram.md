# Exposure, gain and the histogram

Choosing sub exposure length and gain is about collecting enough signal to swamp
the camera's read noise, without clipping stars or wasting dynamic range. Total
integration time (number of subs times sub length) drives the final signal-to-noise
ratio; individual sub length mostly affects how you handle noise and saturation.

## Read the histogram

The histogram shows how many pixels fall at each brightness. For a light frame, the
main peak is the sky background. A common guideline is to expose so the background
peak sits roughly one quarter to one third of the way from the left (black) edge, far enough off the left wall that read noise is swamped, but not so bright that you
lose highlight headroom. If the peak is jammed against the left edge, the sub is too
short; if it is pushed well past the middle, you are wasting dynamic range on sky
glow and will clip star cores sooner.

## Sky-limited exposures

You are "sky-limited" when the noise from the sky background itself is larger than
the camera's read noise. Past that point, longer subs give diminishing returns:
they mostly add saturation risk and lose more subs to satellites, planes, and gusts.
Under heavy light pollution the sky swamps read noise in a short exposure, so short
subs are fine. Under dark skies you need longer subs to get sky-limited. Narrowband
filters block most of the sky, so narrowband subs are typically much longer than
broadband subs from the same site.

## Gain and unity gain

Higher gain lowers read noise (helpful for short subs) but reduces full-well
capacity and dynamic range, so bright stars saturate sooner. "Unity gain" is the
setting where one electron equals roughly one ADU; it is a reasonable default for
deep-sky work on many CMOS cameras. Very high gain settings suit short exposures of
faint targets or fast optics; low gain suits bright targets and preserving star
cores. When in doubt, use the camera's recommended deep-sky gain and adjust sub
length instead.

## More short subs vs fewer long subs

For the same total integration time, many shorter subs and fewer longer subs reach
similar SNR once you are sky-limited, but shorter subs: keep more star cores from
saturating, lose less time to a ruined frame, and are more forgiving of imperfect
guiding. Longer subs: spend a smaller fraction of time on read noise and mean fewer
files to stack. A practical approach is the shortest sub that still gets you
comfortably sky-limited.

## Cooling and dark current

A cooled camera held at a stable set temperature (for example minus 10 Celsius)
keeps dark current low and repeatable, which makes dark-frame calibration work well.
Warmer sensors add thermal noise and hot pixels. Let the cooler reach and hold the
set point before starting, and match your calibration darks to the same temperature,
gain, and exposure.
