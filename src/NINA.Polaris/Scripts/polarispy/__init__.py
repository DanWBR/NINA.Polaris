# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
#
# This program is free software: you can redistribute it and/or modify it
# under the terms of the GNU Affero General Public License as published by
# the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version. See <https://www.gnu.org/licenses/>.

"""polarispy - the Polaris scripting interface.

A small, dependency-free (stdlib only) module that lets a Python script drive
Polaris's processing engine, modelled on Siril's ``sirilpy``. A script imports
``polarispy``, connects, then calls the frame library and the ported Siril
operations (``/api/post/*``), reporting progress and log lines back to the
Polaris web UI.

The Polaris host launches the script in a subprocess with two environment
variables set: ``POLARIS_API_URL`` (the loopback base, e.g.
``http://127.0.0.1:5080``, which is auth-exempt) and ``POLARIS_SCRIPT_JOB`` (the
job id the log / progress back-channel reports against).

Phase 1 is headless: no UI. A declarative, browser-rendered dialog API is
planned for a later phase to replace the native (PyQt6 / tkinter) UI that Siril
scripts use.

    import polarispy
    poe = polarispy.connect()
    poe.log("hello")
    poe.update_progress("working", 0.5)
    frames = poe.list_frames(type="LIGHT")
    poe.stretch(frames[0]["path"], mode="ghs", auto=True)
"""

import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request

__all__ = ["PolarisInterface", "PolarisError", "Dialog", "connect"]


class PolarisError(Exception):
    """Raised when a Polaris API call fails or the host is unreachable."""


def _as_list(paths):
    if isinstance(paths, (list, tuple)):
        return list(paths)
    return [paths]


class PolarisInterface:
    """Connection to the running Polaris host over its loopback HTTP API."""

    def __init__(self, base_url=None, job_id=None):
        self.base = (base_url or os.environ.get("POLARIS_API_URL")
                     or "http://127.0.0.1:5080").rstrip("/")
        self.job = job_id or os.environ.get("POLARIS_SCRIPT_JOB") or ""

    def connect(self):
        """Verify the host is reachable. Returns self so calls can chain."""
        self._request("GET", self.base + "/api/system/version", None)
        return self

    # ---- back-channel to the Polaris UI -----------------------------------
    def log(self, message):
        """Append a line to the script's log (shown live in the UI)."""
        text = str(message)
        print(text, flush=True)   # also captured from stdout as a fallback
        if self.job:
            self._quiet("POST", "/api/script/%s/log" % self.job, {"message": text})

    def update_progress(self, message="", fraction=None):
        """Report progress: a status message and an optional 0..1 fraction."""
        if self.job:
            self._quiet("POST", "/api/script/%s/progress" % self.job,
                        {"message": str(message), "fraction": fraction})

    # ---- frame library ----------------------------------------------------
    def list_frames(self, type=None, target=None, filter=None, limit=100):
        """Query the STUDIO frame library. Returns a list of frame dicts
        (each has at least ``path``, ``id``, ``type``, ``target``, ``filter``)."""
        query = {"limit": limit}
        if type:
            query["type"] = type
        if target:
            query["target"] = target
        if filter:
            query["filter"] = filter
        return self._get("/api/studio/frames", query)

    # ---- processing (ported Siril operations, /api/post/*) ----------------
    def scnr(self, paths, mode="average-neutral", amount=1.0, preserve_lightness=False):
        """Green-cast removal (SCNR) on one or more RGB FITS files."""
        return self._post("/api/post/scnr", {
            "paths": _as_list(paths), "mode": mode, "amount": amount,
            "preserveLightness": preserve_lightness})

    def stretch(self, paths, mode="ghs", d=1.0, b=0.0, lp=0.0, sp=0.0, hp=1.0,
                bp=0.0, auto=False, target_background=0.25):
        """GHS / asinh stretch (linear to stretched)."""
        return self._post("/api/post/stretch", {
            "paths": _as_list(paths), "mode": mode, "d": d, "b": b, "lp": lp,
            "sp": sp, "hp": hp, "bp": bp, "auto": auto,
            "targetBackground": target_background})

    def star_reduce(self, paths, amount=0.5, iterations=1):
        """Reduce star sizes."""
        return self._post("/api/post/star-reduce", {
            "paths": _as_list(paths), "amount": amount, "iterations": iterations})

    def cosmetic(self, paths, **params):
        """Cosmetic (hot / cold pixel) correction."""
        return self._post("/api/post/cosmetic", dict(paths=_as_list(paths), **params))

    def post(self, op, paths, **params):
        """Low-level: call any /api/post/<op> with ``paths`` + params. Lets a
        script reach a Siril-ported operation not yet wrapped by a typed method."""
        return self._post("/api/post/%s" % op.strip("/"),
                          dict(paths=_as_list(paths), **params))

    # ---- UI: a declarative dialog rendered in the Polaris browser ---------
    def dialog(self, title="Polaris script"):
        """Start building a form dialog. Add fields, then call ``.run()`` to show
        it in the Polaris web UI and block until the user submits or cancels."""
        return Dialog(self, title)

    def _run_dialog(self, spec, poll_interval=0.5, timeout=None):
        # No job context (script run standalone / for testing): fall back to the
        # field defaults so the pipeline still runs unattended.
        if not self.job:
            return {f["key"]: f.get("default")
                    for f in spec.get("fields", []) if f.get("key")}
        self._post("/api/script/%s/dialog" % self.job, spec)
        waited = 0.0
        while True:
            r = self._get("/api/script/%s/dialog/result" % self.job)
            if r.get("submitted"):
                return r.get("values") or {}
            if r.get("cancelled"):
                return None
            time.sleep(poll_interval)
            waited += poll_interval
            if timeout and waited >= timeout:
                return None

    # ---- HTTP plumbing ----------------------------------------------------
    def _get(self, path, query=None):
        url = self.base + path
        if query:
            clean = {k: v for k, v in query.items() if v is not None}
            if clean:
                url += "?" + urllib.parse.urlencode(clean)
        return self._request("GET", url, None)

    def _post(self, path, body):
        return self._request("POST", self.base + path, body)

    def _quiet(self, method, path, body):
        try:
            self._request(method, self.base + path, body)
        except Exception:
            pass   # log / progress are best-effort, never fail the script

    def _request(self, method, url, body):
        data = json.dumps(body).encode("utf-8") if body is not None else None
        headers = {"Content-Type": "application/json"} if data is not None else {}
        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=600) as resp:
                raw = resp.read().decode("utf-8")
                return json.loads(raw) if raw else {}
        except urllib.error.HTTPError as exc:
            detail = ""
            try:
                detail = exc.read().decode("utf-8", "replace")
            except Exception:
                pass
            raise PolarisError("%s %s -> HTTP %s: %s" % (method, url, exc.code, detail)) from None
        except urllib.error.URLError as exc:
            raise PolarisError("cannot reach Polaris at %s: %s" % (url, exc)) from None


class Dialog:
    """A declarative form shown in the Polaris browser UI. Build it with the
    field helpers (each returns self so calls chain), then call ``run()``.

        dlg = poe.dialog("Star reduction")
        dlg.slider("amount", "Amount", 0.0, 1.0, 0.5)
        dlg.checkbox("protect_core", "Protect star cores", True)
        values = dlg.run()          # blocks until the user submits / cancels
        if values is None:
            return                  # cancelled
        poe.star_reduce(path, amount=values["amount"])
    """

    def __init__(self, iface, title):
        self._iface = iface
        self.spec = {"title": title, "fields": [], "okLabel": "OK", "cancelLabel": "Cancel"}

    def _add(self, field):
        self.spec["fields"].append(field)
        return self

    def info(self, text):
        """A read-only line of explanatory text."""
        return self._add({"type": "info", "text": str(text)})

    def slider(self, key, label, min=0.0, max=1.0, default=None, step=None):
        return self._add({"type": "slider", "key": key, "label": label,
                          "min": min, "max": max,
                          "step": step if step is not None else (max - min) / 100.0,
                          "default": default if default is not None else min})

    def number(self, key, label, default=0, min=None, max=None, step=1):
        return self._add({"type": "number", "key": key, "label": label,
                          "default": default, "min": min, "max": max, "step": step})

    def checkbox(self, key, label, default=False):
        return self._add({"type": "checkbox", "key": key, "label": label,
                          "default": bool(default)})

    def select(self, key, label, options, default=None):
        opts = list(options)
        return self._add({"type": "select", "key": key, "label": label,
                          "options": opts,
                          "default": default if default is not None else (opts[0] if opts else "")})

    def text(self, key, label, default=""):
        return self._add({"type": "text", "key": key, "label": label, "default": str(default)})

    def buttons(self, ok="OK", cancel="Cancel"):
        self.spec["okLabel"] = ok
        self.spec["cancelLabel"] = cancel
        return self

    def run(self, poll_interval=0.5, timeout=None):
        """Show the dialog and block. Returns a dict of the field values keyed by
        ``key``, or ``None`` if the user cancelled (or ``timeout`` seconds pass)."""
        return self._iface._run_dialog(self.spec, poll_interval, timeout)


def connect(base_url=None, job_id=None):
    """Convenience: build a PolarisInterface and connect. Returns it."""
    return PolarisInterface(base_url, job_id).connect()
