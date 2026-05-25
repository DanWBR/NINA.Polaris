#!/usr/bin/env python3
"""SWE-6 d3-celestial cleanup: surgical removal of dead Celestial.*
helpers from app.js and replacement with stellarium-web-engine bridge
calls. Idempotent — anchors on remaining markers and bails if any
anchor is missing.
"""
import sys

p = 'src/NINA.Polaris/wwwroot/js/app.js'
with open(p, 'r', encoding='utf-8') as f:
    src = f.read()

original_len = len(src)
lines = src.split('\n')


def find_line(needle, start=0):
    for i in range(start, len(lines)):
        if needle in lines[i]:
            return i
    raise ValueError("not found: " + needle)


# --- Block 1: SWE-6 comment + dead rebuildSky/_buildCelestial/
# _centreOnZenith/_computeInitialCentre ---
b1_start = find_line('// SWE-6: rebuildSky() / _buildCelestial() / _centreOnZenith() /')
ret_line = find_line('return [raDeg, poleDec, 0];', b1_start)
close_line = ret_line + 1
assert lines[close_line].strip() == '},', "expected '}}' at {}".format(close_line + 1)
b1_end = close_line + 1
if b1_end < len(lines) and lines[b1_end].strip() == '':
    b1_end += 1
removed1 = lines[b1_start:b1_end]
del lines[b1_start:b1_end]
print("Block1: removed {} lines starting at {}".format(len(removed1), b1_start + 1))

# --- Block 2: _startSkyTicker + _stopSkyTicker ---
b2_anchor = find_line('_startSkyTicker(lat, lng) {')
i = b2_anchor - 1
while i >= 0 and lines[i].strip().startswith('//'):
    i -= 1
b2_start = i + 1
end_marker = find_line('if (this._skyTicker) { clearInterval(this._skyTicker)', b2_anchor)
b2_end = end_marker + 1
assert lines[b2_end].strip() == '},', "expected '}}' at {}, got {!r}".format(b2_end + 1, lines[b2_end])
b2_end += 1
if b2_end < len(lines) and lines[b2_end].strip() == '':
    b2_end += 1
removed2 = lines[b2_start:b2_end]
del lines[b2_start:b2_end]
print("Block2: removed {} lines starting at {}".format(len(removed2), b2_start + 1))

# --- Block 3: setSkyFov() ---
b3_start = find_line('        setSkyFov() {')
j = b3_start + 1
while j < len(lines) and lines[j].strip() != '},':
    j += 1
b3_end = j + 1
if b3_end < len(lines) and lines[b3_end].strip() == '':
    b3_end += 1
removed3 = lines[b3_start:b3_end]
del lines[b3_start:b3_end]
print("Block3: removed {} lines starting at {}".format(len(removed3), b3_start + 1))

# --- Block 4: _projectCelestial..._skyMapCenter (consecutive helpers) ---
b4_start = find_line('// Project an (RA-deg, Dec-deg) celestial coord to screen pixels')
end_smc = find_line('_skyMapCenter()', b4_start)
j = end_smc + 1
while j < len(lines) and lines[j].strip() != '},':
    j += 1
b4_end = j + 1
if b4_end < len(lines) and lines[b4_end].strip() == '':
    b4_end += 1
removed4 = lines[b4_start:b4_end]
del lines[b4_start:b4_end]
print("Block4: removed {} lines starting at {}".format(len(removed4), b4_start + 1))

# --- Block 5: rewrite _goToSelectedTarget body ---
g_start = find_line('_goToSelectedTarget() {')
g_end = find_line('this.updateSkyCameraFov();', g_start)
new_body = [
    '            if (!this.skyTarget) return;',
    '            const ra = this.skyTarget.ra ?? this.skyTarget.raHours;',
    '            const dec = this.skyTarget.dec ?? this.skyTarget.decDeg;',
    '            if (!Number.isFinite(ra) || !Number.isFinite(dec)) return;',
    '            // SWE-6: aim the engine via the postMessage bridge',
    '            // instead of the old Celestial.rotate. FOV defaults to',
    '            // whatever the engine already has (no zoom change).',
    '            this._skyLookAt(ra, Math.max(-89.5, Math.min(89.5, dec)),',
    '                undefined, this.skyTarget.name || null);',
    '            this._pushSkyFovOverlays();',
]
lines[g_start + 1:g_end + 1] = new_body
print("Block5: rewrote _goToSelectedTarget at line {}".format(g_start + 1))

# --- Block 6: simplify _currentSlewTarget ---
cs_start = find_line('_currentSlewTarget() {')
j = cs_start + 1
while j < len(lines) and lines[j].strip() != '},':
    j += 1
cs_end = j  # line with '},'
new_cs = [
    '            // SWE-6: d3-celestial removed; the live map centre is',
    '            // now reachable only async via _skyGetCenter(). Sync',
    '            // callers fall back to the last picked skyTarget.',
    '            const t = this.skyTarget;',
    '            if (t && Number.isFinite(t.ra) && Number.isFinite(t.dec)) {',
    '                return { ra: t.ra, dec: t.dec };',
    '            }',
    '            return null;',
]
lines[cs_start + 1:cs_end] = new_cs
print("Block6: rewrote _currentSlewTarget at line {}".format(cs_start + 1))

# --- Block 7: rewrite _mosaicDrawOverlay ---
md_start = find_line('_mosaicDrawOverlay(plan) {')
# walk to matching '},' tracking brace depth from the '{' on the def line.
depth = 1
j = md_start + 1
md_end = None
while j < len(lines):
    for c in lines[j]:
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                # The closing '}' belongs to the method body. The line is
                # by convention '},' — assert.
                if lines[j].strip() == '},':
                    md_end = j
                    break
    if md_end is not None:
        break
    j += 1
assert md_end is not None, "couldn't find _mosaicDrawOverlay closer"
new_md = [
    '            // SWE-6: push mosaic tiles to the stellarium-web bridge',
    '            // (yellow polygons) as part of the FOV overlay payload.',
    '            // _pushSkyFovOverlays reads this.mosaicTiles and forwards.',
    '            if (!plan?.panels) { this.mosaicTiles = null;',
    '                this._pushSkyFovOverlays(); return; }',
    '            this.mosaicTiles = plan.panels.map(p => ({',
    '                raDeg: p.raHours * 15, decDeg: p.decDeg,',
    '                widthDeg: plan.panelFovWidthDeg,',
    '                heightDeg: plan.panelFovHeightDeg,',
    '                rotationDeg: 0',
    '            }));',
    '            this._pushSkyFovOverlays();',
]
lines[md_start + 1:md_end] = new_md
print("Block7: rewrote _mosaicDrawOverlay at line {}".format(md_start + 1))

# --- Block 8: include mosaic in set-fov-overlays send ---
needle = "this._skySendMessage({ type: 'set-fov-overlays', mount, target });"
for i in range(len(lines)):
    if needle in lines[i]:
        indent = lines[i][:len(lines[i]) - len(lines[i].lstrip())]
        replacement = (
            "this._skySendMessage({ type: 'set-fov-overlays', mount, target,\n"
            + indent
            + "    mosaic: this.mosaicTiles && this.mosaicTiles.length\n"
            + indent
            + "        ? { tiles: this.mosaicTiles } : null });"
        )
        lines[i] = lines[i].replace(needle, replacement)
        print("Block8: extended _pushSkyFovOverlays send at line {}".format(i + 1))
        break

new_src = '\n'.join(lines)
with open(p, 'w', encoding='utf-8') as f:
    f.write(new_src)
print("Done: {} -> {} bytes ({} fewer)".format(
    original_len, len(new_src), original_len - len(new_src)))
