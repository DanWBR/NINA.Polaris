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
import ssl
import time
import urllib.error
import urllib.parse
import urllib.request

__all__ = ["PolarisInterface", "PolarisError", "Dialog", "connect"]

# Polaris talks to us over the loopback API (127.0.0.1). Its HTTPS redirect can
# bounce the plain-HTTP loopback call to the self-signed TLS endpoint, which the
# default urllib would reject. Loopback is same-machine and the cert is Polaris's
# own, so verification is safely disabled here (never used for non-loopback).
_SSL_NOVERIFY = ssl.create_default_context()
_SSL_NOVERIFY.check_hostname = False
_SSL_NOVERIFY.verify_mode = ssl.CERT_NONE


class PolarisError(Exception):
    """Raised when a Polaris API call fails or the host is unreachable."""


def _as_list(paths):
    if isinstance(paths, (list, tuple)):
        return list(paths)
    return [paths]


def _require_numpy_astropy():
    """Import numpy + astropy lazily. Pixel-level access needs them, but the
    core (Phase 1/2) stays dependency-free, so import only when actually used."""
    try:
        import numpy as np
        from astropy.io import fits
        return np, fits
    except ImportError as exc:
        raise PolarisError(
            "pixel access needs numpy and astropy. Install them into the host "
            "Python: pip install numpy astropy  (%s)" % exc) from None


def _default_out(src):
    import os
    root, ext = os.path.splitext(src)
    return root + "_polarispy" + (ext or ".fits")


class PolarisInterface:
    """Connection to the running Polaris host over its loopback HTTP API."""

    def __init__(self, base_url=None, job_id=None):
        self.base = (base_url or os.environ.get("POLARIS_API_URL")
                     or "http://127.0.0.1:5080").rstrip("/")
        self.job = job_id or os.environ.get("POLARIS_SCRIPT_JOB") or ""
        # Working image: defaults to the frame the user had open in STUDIO when
        # they launched the script (Siril's "currently loaded image"), if any.
        self.current = os.environ.get("POLARIS_ACTIVE_FRAME") or None

    def connect(self):
        """Verify the host is reachable. Returns self so calls can chain."""
        self._request("GET", self.base + "/api/system/status", None)
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

    # ---- STUDIO context (like Siril's open frame / home folder) -----------
    def active_frame(self):
        """The frame the user had open in STUDIO when launching the script, or
        None. It is also the default working image (see load())."""
        return os.environ.get("POLARIS_ACTIVE_FRAME") or None

    def cwd(self):
        """The folder the user was browsing in the STUDIO Files tab, or None."""
        return os.environ.get("POLARIS_CWD") or None

    def home(self):
        """The STUDIO root (the capture home / ImageOutputDir). Siril scripts
        that work over the 'home folder' should walk this or use list_frames()."""
        r = self._get("/api/files/studio-root")
        return r.get("effective") or r.get("configured") or None

    # ---- working image (Siril-style: load once, then operate) -------------
    def load(self, path):
        """Set the working image. Processing ops and pixel access default to it
        when their ``paths``/``path`` argument is omitted."""
        self.current = path
        return path

    def _targets(self, paths):
        if paths is None:
            if not self.current:
                raise PolarisError("no image loaded; call load(path) or pass a path")
            return [self.current]
        return _as_list(paths)

    # ---- processing (ported Siril operations, /api/post/*) ----------------
    # ``paths`` may be omitted to operate on the loaded image (see load()).
    def scnr(self, paths=None, mode="average-neutral", amount=1.0, preserve_lightness=False):
        """Green-cast removal (SCNR) on one or more RGB FITS files."""
        return self._post("/api/post/scnr", {
            "paths": self._targets(paths), "mode": mode, "amount": amount,
            "preserveLightness": preserve_lightness})

    def stretch(self, paths=None, mode="ghs", d=1.0, b=0.0, lp=0.0, sp=0.0, hp=1.0,
                bp=0.0, auto=False, target_background=0.25):
        """GHS / asinh stretch (linear to stretched)."""
        return self._post("/api/post/stretch", {
            "paths": self._targets(paths), "mode": mode, "d": d, "b": b, "lp": lp,
            "sp": sp, "hp": hp, "bp": bp, "auto": auto,
            "targetBackground": target_background})

    def star_reduce(self, paths=None, amount=0.5, iterations=1):
        """Reduce star sizes."""
        return self._post("/api/post/star-reduce", {
            "paths": self._targets(paths), "amount": amount, "iterations": iterations})

    def cosmetic(self, paths=None, **params):
        """Cosmetic (hot / cold pixel) correction."""
        return self._post("/api/post/cosmetic", dict(paths=self._targets(paths), **params))

    def post(self, op, paths=None, **params):
        """Low-level: call any /api/post/<op> with ``paths`` + params. Lets a
        script reach a Siril-ported operation not yet wrapped by a typed method."""
        return self._post("/api/post/%s" % op.strip("/"),
                          dict(paths=self._targets(paths), **params))

    def rescan(self):
        """Re-index the STUDIO frame library (call after writing a new file)."""
        return self._post("/api/studio/rescan", {})

    # ---- pixel data as numpy (needs numpy + astropy) ----------------------
    # The script runs on the Polaris host, so it reads/writes the FITS file
    # directly rather than transferring pixels over HTTP.
    def get_pixeldata(self, path=None):
        """Return the image pixels as a numpy array (2D mono, or 3D for colour),
        or None when the file has no data. Needs numpy + astropy."""
        src = path or self.current
        if not src:
            raise PolarisError("no image; call load(path) or pass path")
        np, fits = _require_numpy_astropy()
        try:
            with fits.open(src) as hdul:
                hdu = next((h for h in hdul if getattr(h, "data", None) is not None), None)
                return None if hdu is None else np.array(hdu.data)
        except PolarisError:
            raise
        except Exception as exc:
            raise PolarisError("cannot read pixels from %s: %s" % (src, exc)) from None

    def set_pixeldata(self, data, path=None, out_path=None, rescan=True):
        """Write a numpy array back to a FITS (preserving the source header) and
        re-index STUDIO. Returns the written path. Needs numpy + astropy."""
        src = path or self.current
        if not src:
            raise PolarisError("no image; call load(path) or pass path")
        _np, fits = _require_numpy_astropy()
        out = out_path or _default_out(src)
        try:
            with fits.open(src) as hdul:
                hdu = next((h for h in hdul if getattr(h, "data", None) is not None), hdul[0])
                hdu.data = data
                # Keep the source header (BAYERPAT etc.) but, for a float result,
                # drop the integer BZERO/BSCALE so astropy writes the real dtype
                # (BITPIX -32) instead of quantising it back through the int scaling.
                if getattr(data, "dtype", None) is not None and data.dtype.kind == "f":
                    for _k in ("BZERO", "BSCALE"):
                        if _k in hdu.header:
                            del hdu.header[_k]
                hdul.writeto(out, overwrite=True)
        except Exception as exc:
            raise PolarisError("cannot write %s: %s" % (out, exc)) from None
        if rescan:
            try: self.rescan()
            except Exception: pass
        return out

    # sirilpy-flavoured aliases (used by the compat shim / ported scripts).
    def get_image_pixeldata(self, *a, **k): return self.get_pixeldata(*a, **k)
    def set_image_pixeldata(self, data, *a, **k): return self.set_pixeldata(data, *a, **k)

    def cmd(self, name, paths=None, **params):
        """Run a processing op by name on the loaded image (or ``paths``),
        Siril-cmd flavoured. Maps a curated set of names to the /api/post ops;
        raises for anything not supported by Polaris."""
        alias = {"ght": "stretch", "ghs": "stretch", "autostretch": "stretch",
                 "asinh": "stretch", "rmgreen": "scnr", "scnr": "scnr",
                 "starnet": "star-reduce", "unclipstars": "star-reduce"}
        op = alias.get(name.lower(), name.lower())
        known = {"scnr", "stretch", "star-reduce", "cosmetic", "wavelet-sharpen",
                 "wavescale-hdr", "clahe", "highlight-recovery"}
        if op not in known:
            raise PolarisError("cmd('%s') is not supported by Polaris" % name)
        return self.post(op, paths, **params)

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
        # Polaris's UseHttpsRedirect answers the loopback HTTP port with a 307 to
        # HTTPS. urllib auto-follows 307 for GET but refuses to for POST, so we
        # follow it ourselves (method + body preserved) and pin self.base to the
        # redirected origin so later requests skip the round trip.
        for _ in range(4):
            req = urllib.request.Request(url, data=data, method=method, headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=600, context=_SSL_NOVERIFY) as resp:
                    raw = resp.read().decode("utf-8")
                    return json.loads(raw) if raw else {}
            except urllib.error.HTTPError as exc:
                if exc.code in (301, 302, 307, 308) and exc.headers.get("Location"):
                    loc = urllib.parse.urljoin(url, exc.headers["Location"])
                    parts = urllib.parse.urlsplit(loc)
                    self.base = "%s://%s" % (parts.scheme, parts.netloc)
                    url = loc
                    continue
                detail = ""
                try:
                    detail = exc.read().decode("utf-8", "replace")
                except Exception:
                    pass
                raise PolarisError("%s %s -> HTTP %s: %s" % (method, url, exc.code, detail)) from None
            except urllib.error.URLError as exc:
                raise PolarisError("cannot reach Polaris at %s: %s" % (url, exc)) from None
        raise PolarisError("%s %s -> too many redirects" % (method, url))


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
