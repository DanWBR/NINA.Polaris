# Light pollution and filters

Where you image and what filter you use decide how much sky glow ends up in your
data. You cannot process away noise you never separated from the target, so cutting
light pollution at capture time is worth more than any software fix.

## Bortle scale

The Bortle scale rates night-sky darkness from 1 (pristine dark site, Milky Way
casts shadows) to 9 (inner-city sky). Most suburban backyards are Bortle 6 to 8.
Darker skies mean fainter targets are reachable, broadband subs can be longer before
the sky swamps them, and galaxies and faint nebulosity are far easier. If you can
travel to a darker site occasionally, broadband targets benefit the most.

## Broadband vs narrowband

Broadband imaging (LRGB or one-shot color without a line filter) captures the target
across the whole visible spectrum and is very sensitive to light pollution, because
streetlights emit broadband too. Narrowband imaging isolates specific emission lines
(hydrogen-alpha at 656 nm, oxygen-III near 500 nm, sulfur-II at 672 nm). Emission
nebulae glow strongly in these lines, while most light pollution does not, so
narrowband cuts through city skies and even moonlight. Galaxies and reflection
nebulae shine by broadband/starlight, so narrowband does little for them.

## Filter types

- Light-pollution / broadband "city" filters knock down the specific bands of old
  sodium and mercury streetlights. They help less against modern broadband LED
  lighting, and they slightly shift color balance.
- Dual-band or multiband filters (for example Ha + OIII in one filter) let a one-shot
  color camera capture two narrowband lines at once. They are the easiest big upgrade
  for OSC imagers under light pollution, great on emission nebulae.
- Narrowband line filters (3 to 7 nm) for mono cameras give the deepest contrast and
  the classic Hubble-palette (SHO) images, at the cost of a filter wheel, longer
  exposures, and separate integration per line.
- UV/IR cut filters are needed on many refractors to keep bloated star halos in
  check and are standard for broadband color.

## The Moon

Moonlight is broadband and raises the whole sky background. Around full Moon, do
narrowband targets or bright objects, and save faint broadband galaxies and
nebulosity for the days around new Moon. Narrowband filters largely ignore moonlight,
which is why they are the go-to for imaging every night of the month.
