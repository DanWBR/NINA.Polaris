# Calibration frames

Calibration frames remove the camera's and optical train's fixed signatures from
your light frames, so stacking is cleaner and gradients from the sensor and dust are
corrected. The three types are darks, flats, and bias (or dark-flats).

## Flats

Flats correct uneven illumination: vignetting (darker corners) and dust shadows
("dust bunnies"). You shoot an evenly lit, featureless surface through the exact same
optical train, focus, and camera rotation as your lights. Aim for an exposure that
puts the histogram peak around one third to one half of full scale, not clipped. Take
20 to 40 and stack them. Because flats depend on the exact dust and orientation, shoot
new flats whenever you refocus significantly, rotate the camera, or reassemble the
train. Sky flats (twilight), a flat panel, or an evenly lit white screen all work.

## Darks

Darks capture the sensor's dark current and hot pixels with the shutter closed (no
light), at the same exposure time, gain, and temperature as your lights. Stacking 20
to 50 darks into a master dark and subtracting it removes hot pixels and amp glow.
Cooled cameras make darks reliable because you can reproduce the exact temperature;
a dark library shot at your usual settings can be reused for weeks. Uncooled or DSLR
sensors change with ambient temperature, so darks must match the night.

## Bias and dark-flats

Bias frames are the shortest possible exposure with no light; they record the
sensor's read offset. They calibrate flats and, in some workflows, scale darks. Many
modern CMOS cameras behave better with dark-flats (darks at the flat exposure time)
instead of bias for calibrating flats. If your flats look over- or under-corrected,
switching between bias and dark-flats is a common fix.

## Do you always need all of them?

Flats give the biggest visible improvement and are worth taking every session.
Dithering plus a good sigma-clipping stack can substitute for darks on many CMOS
cameras by rejecting hot pixels statistically, but darks still help with amp glow and
consistency. At minimum, take flats. Add darks (or dark-flats) when you see hot
pixels, amp glow, or residual gradients the stack cannot reject.
