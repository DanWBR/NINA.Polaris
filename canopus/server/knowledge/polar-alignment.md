# Polar alignment

An equatorial mount tracks the sky by turning one axis (the RA axis) to counter
Earth's rotation. That only works if the RA axis points at the celestial pole.
Polar alignment is the act of aiming it there. Good polar alignment gives round
stars, low declination drift, and lets guiding do its job with gentle corrections.

## Why it matters

If the polar axis is off, stars slowly drift in declination and the whole field
rotates around the guide star, so even perfectly guided subs show field rotation at
the edges. A small error is fine for short unguided subs; longer subs and wider
fields demand tighter alignment. As a rough guide, a few arcminutes of error is
acceptable for guided imaging, while unguided imaging wants under about an arcminute.

## Methods

- Polar scope: a reticle in the mount's hollow RA axis that you align to Polaris (or
  Sigma Octantis in the south) using a date/time dial or an app. Quick and good
  enough for guided work.
- Drift alignment: watch a star's declination drift near the meridian to fix azimuth,
  then near the horizon (east or west) to fix altitude. Slow but very accurate, needs
  no view of the pole.
- Plate-solve-assisted alignment (for example a three-point routine): the software
  takes exposures at a few RA positions, plate-solves each, computes where the axis
  actually points, and tells you exactly how much to turn the azimuth and altitude
  knobs. Fast and accurate, works even without seeing the pole, and is the easiest
  modern method.

## Practical tips

Level the tripod first and set the mount's latitude to your site's latitude as a
starting point. Adjust only the azimuth and altitude bolts during alignment, not the
tripod. Make small, deliberate moves and re-measure. Do not over-tighten. Once
aligned, avoid bumping the tripod. If you tear down and set up in the same spot each
night, marking the tripod leg positions saves time.

## Southern hemisphere

There is no bright pole star in the south; the pole sits in a faint region near
Sigma Octantis. Polar-scope alignment is harder, so plate-solve-assisted or drift
alignment is especially useful below the equator.
