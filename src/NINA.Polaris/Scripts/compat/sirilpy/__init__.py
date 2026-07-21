# N.I.N.A. Polaris
# Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
#
# This program is free software: you can redistribute it and/or modify it
# under the terms of the GNU Affero General Public License as published by
# the Free Software Foundation, either version 3 of the License, or (at your
# option) any later version. See <https://www.gnu.org/licenses/>.

"""Partial sirilpy compatibility shim, backed by polarispy.

Lets the *processing* half of a Siril Python script run on Polaris with minimal
changes: ``import sirilpy`` resolves here (this folder is ahead of site-packages
on the script's PYTHONPATH), and ``SirilInterface`` forwards to polarispy.

Only a subset of the real sirilpy is implemented, and the **UI half is not**:
Siril scripts build native PyQt6 / tkinter windows, which cannot render in the
browser. Port those parts to ``polarispy.Dialog`` (a declarative form the
Polaris web UI renders). Unimplemented UI helpers raise a clear error pointing
there.

    import sirilpy
    siril = sirilpy.SirilInterface()
    siril.connect()
    siril.image_load(path)
    data = siril.get_image_pixeldata()      # numpy (needs numpy + astropy)
    siril.set_image_pixeldata(data * 0.9)
    siril.log("done")
"""

import polarispy

__all__ = ["SirilInterface", "PolarisError"]

PolarisError = polarispy.PolarisError


class SirilInterface:
    """Drop-in-ish stand-in for sirilpy.SirilInterface, forwarding to polarispy."""

    def __init__(self, *args, **kwargs):
        self._poe = None

    def connect(self, *args, **kwargs):
        self._poe = polarispy.connect()
        return True

    # --- commands / images -------------------------------------------------
    def cmd(self, name, *args, **kwargs):
        # Real sirilpy passes Siril command-line tokens; here keyword params map
        # to the Polaris op. Positional tokens are ignored (best effort).
        return self._poe.cmd(name, **kwargs)

    def image_load(self, path, *args, **kwargs):
        return self._poe.load(path)

    def get_image_pixeldata(self, *args, **kwargs):
        return self._poe.get_pixeldata(*args, **kwargs)

    def set_image_pixeldata(self, data, *args, **kwargs):
        return self._poe.set_pixeldata(data, *args, **kwargs)

    # --- logging / progress ------------------------------------------------
    def log(self, message, *args, **kwargs):
        return self._poe.log(message)

    def update_progress(self, message="", progress=None, *args, **kwargs):
        return self._poe.update_progress(message, progress)

    # --- UI: not available on Polaris -------------------------------------
    def _no_ui(self, *args, **kwargs):
        raise NotImplementedError(
            "sirilpy native UI is not available on Polaris. Build the dialog with "
            "polarispy.Dialog instead (poe.dialog(...).slider(...).run()).")

    error_messagebox = _no_ui
    info_messagebox = _no_ui
    warning_messagebox = _no_ui
    def __getattr__(self, name):
        # Any other sirilpy method a script reaches for is a clear no-op error.
        raise AttributeError(
            "sirilpy.%s is not implemented by the Polaris shim. Supported: connect, "
            "cmd, image_load, get/set_image_pixeldata, log, update_progress." % name)
