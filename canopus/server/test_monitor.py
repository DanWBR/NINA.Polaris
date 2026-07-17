# N.I.N.A. Polaris — Canopus Assistant
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
#
# This program is free software: you can redistribute it and/or modify it
# under the terms of the GNU Affero General Public License as published by
# the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version.
#
# This program is distributed in the hope that it will be useful, but WITHOUT
# ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
# FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
# for more details. You should have received a copy of the license along with
# this program. If not, see <https://www.gnu.org/licenses/>.
#
# Unit tests for the rule-based real-time session monitor (PA-P4).
#
#   python -m pytest server/test_monitor.py     (or: python server/test_monitor.py)

from monitor import StatusMonitor


def keys(alerts):
    return {a.key for a in alerts}


def test_first_snapshot_is_a_baseline_no_alerts():
    m = StatusMonitor()
    out = m.evaluate({"guider": {"appState": "Guiding", "guiding": True}}, now=0)
    assert out == []


def test_guiding_lost_edge_triggered():
    m = StatusMonitor()
    m.evaluate({"guider": {"appState": "Guiding", "guiding": True}}, now=0)
    out = m.evaluate({"guider": {"appState": "LostLock", "guiding": True}}, now=1)
    assert "guiding_lost" in keys(out)
    # Staying lost does not refire (edge-triggered).
    out2 = m.evaluate({"guider": {"appState": "LostLock", "guiding": True}}, now=2)
    assert "guiding_lost" not in keys(out2)


def test_rms_spike_crossing_up_only():
    m = StatusMonitor()
    m.evaluate({"guider": {"guiding": True, "rmsTotal": 0.8}}, now=0)
    out = m.evaluate({"guider": {"guiding": True, "rmsTotal": 3.1}}, now=1)
    assert "rms_high" in keys(out)
    # Already-high on the next tick must not refire.
    out2 = m.evaluate({"guider": {"guiding": True, "rmsTotal": 3.4}}, now=2)
    assert "rms_high" not in keys(out2)


def test_rms_spike_ignored_when_not_guiding():
    m = StatusMonitor()
    m.evaluate({"guider": {"guiding": False, "rmsTotal": 0.5}}, now=0)
    out = m.evaluate({"guider": {"guiding": False, "rmsTotal": 4.0}}, now=1)
    assert "rms_high" not in keys(out)


def test_mount_untracking_unexpected():
    m = StatusMonitor()
    m.evaluate({"mount": {"connected": True, "tracking": True, "slewing": False}}, now=0)
    out = m.evaluate({"mount": {"connected": True, "tracking": False, "slewing": False}}, now=1)
    assert "mount_untracking" in keys(out)
    # Stopping tracking *because* of a slew is not flagged.
    m2 = StatusMonitor()
    m2.evaluate({"mount": {"connected": True, "tracking": True, "slewing": False}}, now=0)
    out2 = m2.evaluate({"mount": {"connected": True, "tracking": False, "slewing": True}}, now=1)
    assert "mount_untracking" not in keys(out2)


def test_camera_disconnect():
    m = StatusMonitor()
    m.evaluate({"camera": {"connected": True}}, now=0)
    out = m.evaluate({"camera": {"connected": False}}, now=1)
    assert "camera_disconnect" in keys(out)


def test_meridian_heads_up_crossing_window():
    m = StatusMonitor()
    m.evaluate({"meridian": {"minutesToFlip": 20}}, now=0)
    out = m.evaluate({"meridian": {"minutesToFlip": 8}}, now=1)
    assert "meridian_soon" in keys(out)
    # After the flip, minutesToFlip resets high; no alert on the way up.
    out2 = m.evaluate({"meridian": {"minutesToFlip": 300}}, now=2)
    assert "meridian_soon" not in keys(out2)


def test_hfr_climb():
    m = StatusMonitor()
    m.evaluate({"focus": {"hfr": 3.0}}, now=0)
    out = m.evaluate({"focus": {"hfr": 4.6}}, now=1)   # >1.4x and >= floor
    assert "hfr_climb" in keys(out)


def test_cooldown_blocks_refire_within_window():
    m = StatusMonitor(cooldown_s=300)
    m.evaluate({"camera": {"connected": True}}, now=0)
    a = m.evaluate({"camera": {"connected": False}}, now=1)
    assert "camera_disconnect" in keys(a)
    # Reconnect then disconnect again inside the cooldown -> suppressed.
    m.evaluate({"camera": {"connected": True}}, now=2)
    b = m.evaluate({"camera": {"connected": False}}, now=3)
    assert "camera_disconnect" not in keys(b)
    # Same transition after the cooldown -> fires again.
    m.evaluate({"camera": {"connected": True}}, now=400)
    c = m.evaluate({"camera": {"connected": False}}, now=401)
    assert "camera_disconnect" in keys(c)


if __name__ == "__main__":
    for name, fn in sorted(globals().items()):
        if name.startswith("test_") and callable(fn):
            fn()
            print("PASS", name)
    print("ALL PASS")
