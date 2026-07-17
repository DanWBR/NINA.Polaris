# Autoguiding

Autoguiding keeps a star locked on the same pixel through a long exposure by
sending small correction pulses to the mount. Good guiding is what lets you take
minutes-long subs with round, tight stars.

## Reading guide RMS

Guiding error is reported as RMS (root mean square) in arcseconds, split into RA
and DEC. A useful rule of thumb: total RMS should be comfortably smaller than your
imaging resolution (arcsec per pixel). Under about 1 arcsec total RMS is good for
most small refractors; under 0.5 arcsec is excellent. What matters most is that
stars stay round, not a specific number. Elongated stars in one axis point to a
problem in that axis (RA or DEC).

## Calibration

Before guiding, the software calibrates by pulsing the mount in each direction and
measuring how far and which way the star moves. Calibrate near the celestial
equator and meridian (around DEC 0, near the meridian) where the sky moves fastest,
so the calibration is accurate. Recalibrate after a meridian flip unless your
software applies the flip automatically, and after any change to the guide scope or
camera orientation.

## Common guiding problems

- Oscillation (the star swings back and forth across the target): aggressiveness is
  too high, or you are chasing the seeing. Lower the aggressiveness and increase the
  minimum move so tiny jitters are ignored.
- Slow drift in DEC: usually polar alignment error. Improve polar alignment; a
  little declination drift is normal and guiding corrects it, but large drift needs
  frequent corrections and risks star trails.
- Sudden jumps or lost star: cable snags, wind, a stiff spot in the gears, or a
  passing cloud. Check cables have slack, shield from wind.
- Backlash in DEC: when the mount reverses direction it does nothing for a moment.
  Guide DEC in one direction only, or measure and set the backlash compensation.

## Dithering

Dithering shifts the mount by a few pixels between subframes so that fixed-pattern
noise, hot pixels, and walking noise land in different places each frame and average
out when you stack. Always dither if you are stacking many subs. Dither by a few
pixels and let guiding settle before the next exposure starts. Dither less often
(every 2 to 3 frames) if settling time is eating your session.

## Guiding without a guide scope

Native or on-camera guiding (pulse guiding driven by star motion in the main or a
built-in guider) can work when a separate guide scope is not practical. It follows
the same principles: calibrate, keep corrections gentle, and watch RMS. Differential
flexure between a guide scope and the main scope is avoided with an off-axis guider,
which picks the guide star off the main optical path.
