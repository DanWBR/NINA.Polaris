# Focusing

Sharp focus is the single biggest factor in how good your subframes look. A tiny
focus error spreads every star into a fatter disk and throws away fine detail
that no amount of processing can recover.

## How to judge focus

The most reliable numeric measure is HFR (half-flux radius): the radius that
contains half of a star's light. Lower HFR means tighter stars and better focus.
Watch the HFR value as you move the focuser and look for the minimum. FWHM (full
width at half maximum) is a related measure; both drop to a minimum at best focus
and rise on either side, tracing a V or U shaped curve.

Don't judge focus by eye on a stretched preview alone: at night, screen stretch
and seeing make near-focus positions look identical. Trust the HFR number.

## Autofocus routine

An autofocus run samples HFR at several focuser positions on both sides of focus,
fits a curve (a parabola or hyperbola), and moves to the modelled minimum. For a
good run: use a moderately short exposure (2 to 5 seconds is usually enough to get
several stars), make sure the field has stars, and keep the step size large enough
that the outer points are clearly defocused. If the fit quality (R squared) is
poor, the curve was too flat or too noisy; increase exposure or step size and
retry.

## When to refocus

Focus drifts as the temperature drops through the night because the tube and
focuser shrink. Refocus every time the temperature falls by about 1 to 2 degrees
Celsius, after a filter change (each filter can have a different focus offset), and
after a meridian flip. Refocusing every 30 to 60 minutes is a safe default if you
are not tracking temperature.

## Bahtinov mask

A Bahtinov mask is a slotted mask you put over the front of the scope. It turns a
bright star into a three-line diffraction pattern; when the central spike is
exactly centered between the other two, you are in focus. It is a quick, cheap way
to nail focus on a bright star, useful for a first rough focus before a numeric
autofocus run.

## Common focus mistakes

Backlash in the focuser can make the curve asymmetric or shift best focus: always
approach focus from the same direction. A tilted sensor or optical train makes one
corner focus at a different point than the opposite corner; that is tilt, not a
focus error, and no single focus position fixes it. Dew on the corrector or lens
softens stars and mimics bad focus; use a dew heater.
