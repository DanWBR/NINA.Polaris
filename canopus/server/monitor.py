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
# Canopus Assistant — real-time session monitor (PA-P4).
#
# The browser bridge forwards a compact rig snapshot (see agent-protocol.md,
# `status` message) every couple of seconds while the assistant is open. This
# module turns a stream of those snapshots into occasional, actionable
# `notice` nudges — "guiding just dropped", "meridian flip in 8 min" — WITHOUT
# calling the LLM. It's pure rule logic (cheap, deterministic, testable): the
# agent runs it on each snapshot and forwards any fired alerts to the user.
#
# Two guards keep it from being noisy:
#   - edge-triggered: a rule fires on the TRANSITION into a bad state, not
#     every tick it stays there;
#   - per-key cooldown: the same alert can't refire for `cooldown_s` seconds,
#     so a value flapping around a threshold doesn't spam the chat.

from __future__ import annotations

from dataclasses import dataclass


@dataclass
class Alert:
    key: str
    text: str
    severity: str = "warn"   # "warn" | "info"


def _get(d, *path, default=None):
    """Safe nested-dict getter: _get(snap, 'guider', 'appState')."""
    for p in path:
        if not isinstance(d, dict):
            return default
        d = d.get(p)
    return default if d is None else d


def _num(v):
    """Coerce to float, or None if it isn't a finite number."""
    try:
        f = float(v)
    except (TypeError, ValueError):
        return None
    if f != f or f in (float("inf"), float("-inf")):
        return None
    return f


class StatusMonitor:
    # Thresholds. Deliberately conservative so nudges are rare and trustworthy.
    RMS_PX = 2.5             # guiding RMS (px) considered a spike
    HFR_JUMP = 1.4           # HFR growth factor that flags focus drift
    HFR_FLOOR = 3.0          # ...only once stars are at least this soft (px)
    MERIDIAN_MIN = 10        # minutes-to-flip that trips the heads-up
    COOLDOWN_S = 300         # per-alert refire lockout

    def __init__(self, cooldown_s: float = COOLDOWN_S) -> None:
        self._prev: dict | None = None
        self._last_fired: dict[str, float] = {}
        self._cooldown = cooldown_s

    def evaluate(self, snap: dict, now: float) -> list[Alert]:
        """Fold one snapshot in and return the alerts to surface right now
        (edge-triggered vs. the previous snapshot, then cooldown-filtered)."""
        prev, self._prev = self._prev, snap
        if prev is None:
            return []   # need a baseline before we can detect transitions
        fired = []
        for a in self._rules(prev, snap):
            if now - self._last_fired.get(a.key, float("-inf")) >= self._cooldown:
                self._last_fired[a.key] = now
                fired.append(a)
        return fired

    def _rules(self, prev: dict, cur: dict) -> list[Alert]:
        out: list[Alert] = []

        # Guiding lost the star.
        if (_get(cur, "guider", "appState") == "LostLock"
                and _get(prev, "guider", "appState") != "LostLock"):
            out.append(Alert("guiding_lost",
                "⚠️ Guiding just lost the star (LostLock). Subs may trail "
                "until it recovers — want me to pause the sequence?"))

        # Guiding RMS spiked (crossing up), only while actually guiding.
        rc, rp = _num(_get(cur, "guider", "rmsTotal")), _num(_get(prev, "guider", "rmsTotal"))
        if (rc is not None and rc > self.RMS_PX
                and (rp is None or rp <= self.RMS_PX)
                and _get(cur, "guider", "guiding")):
            out.append(Alert("rms_high",
                f"⚠️ Guiding RMS jumped to {rc:.1f} px. Wind, a cable snag, "
                "or seeing — watch the star shape on the next few subs."))

        # Mount stopped tracking without slewing (unexpected).
        if (_get(prev, "mount", "tracking") and not _get(cur, "mount", "tracking")
                and not _get(cur, "mount", "slewing")
                and _get(cur, "mount", "connected")):
            out.append(Alert("mount_untracking",
                "⚠️ The mount stopped tracking. If that wasn't you, the "
                "target is drifting out of frame."))

        # Camera dropped off the bus.
        if _get(prev, "camera", "connected") and not _get(cur, "camera", "connected"):
            out.append(Alert("camera_disconnect",
                "⚠️ The camera just disconnected — capture will stall "
                "until it reconnects."))

        # Meridian flip coming up (crossing down through the window).
        mc, mp = _num(_get(cur, "meridian", "minutesToFlip")), _num(_get(prev, "meridian", "minutesToFlip"))
        if (mc is not None and 0 < mc <= self.MERIDIAN_MIN
                and (mp is None or mp > self.MERIDIAN_MIN)):
            out.append(Alert("meridian_soon",
                f"\U0001f52d About {mc:.0f} min to the meridian flip. Guiding and "
                "framing re-settle afterwards; I can keep watch.", severity="info"))

        # Focus drift: stars growing well beyond the last reading.
        hc, hp = _num(_get(cur, "focus", "hfr")), _num(_get(prev, "focus", "hfr"))
        if (hc is not None and hp is not None and hp > 0
                and hc >= self.HFR_FLOOR and hc > hp * self.HFR_JUMP):
            out.append(Alert("hfr_climb",
                f"⚠️ Star sizes are growing (HFR {hp:.1f} → {hc:.1f} px). "
                "Focus drift with temperature, or dew — a refocus may help."))

        return out
