# Stacking and subframe grading

Stacking (integration) combines many calibrated light frames of the same target into
one deeper image. Signal from the object adds up while random noise partly cancels, so
the stack has a much better signal-to-noise ratio than any single sub. The frames must
first be aligned (registered) so the stars land on top of each other, then reduced per
pixel across all frames. In Polaris this offline integration lives in the STUDIO tab and
produces a `master_light` FITS you then stretch and process.

## Why grade subs before stacking

Not every sub is worth keeping. A passing cloud, a wind gust, a bad guiding excursion,
or a satellite trail can wreck individual frames. Averaging a soft or trailed sub into
the stack drags down the whole result. Grading measures each sub's quality and lets you
keep only the good ones. The two metrics that matter most:

- **HFR (Half-Flux Radius)**: how tight the stars are. Lower is sharper. Rising HFR means
  soft focus, poor seeing, wind, or trailing.
- **Star count**: how many stars were detected. A sudden drop usually means clouds or
  haze cutting transparency, even if the stars that remain look sharp.

A good default is to keep frames within about 20% of the best HFR and drop any with far
fewer stars than the median (the cloudy ones). Keeping roughly the best 70 to 90% of a
night is typical; throwing away too much just makes the stack shallower.

## Integration method

- **Sigma-clipped mean** (default): averages each pixel but first rejects statistical
  outliers, so satellite trails, planes, and cosmic-ray hits are removed. Best all-round
  choice once you have roughly ten or more subs.
- **Mean**: plain average. Highest signal-to-noise but keeps outliers, so only use it on
  data you know is clean.
- **Median**: rejects outliers strongly but is noisier than sigma clipping. Useful with
  very few subs or heavy trail contamination.

## Drizzle

Drizzle integrates onto a finer grid (2x or 3x). It only helps **undersampled** data
(stars spread over fewer than about two pixels) that was **well dithered** between subs.
On well-sampled data it just enlarges the image and amplifies noise, so leave it at 1x
unless you know the data is undersampled.

## Dithering and calibration first

Dithering (a small random mount move between subs) is what lets sigma-clipping and
drizzle work well, because it decorrelates the fixed-pattern noise and walking noise
between frames. And stacking is only as clean as its calibration: subtract darks and
divide by flats before integrating, or fixed hot pixels and vignetting survive into the
master. See the calibration-frames note for how those are built.
