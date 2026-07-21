# polaris: name=Dual-Band Extract; icon=🧪; scope=frame
"""Extract Ha and OIII from a dual-band OSC image (DBXtract, ported to polarispy).

Sensor-aware narrowband extraction for dual-band filters (Ha + OIII, e.g.
L-eXtreme / L-eNhance) applied to a debayered colour image. Per-sensor quantum
efficiency coefficients unmix the R/G/B channels into mono Ha and OIII frames,
written next to the source. Needs numpy + astropy on the host (install the
scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: DBXtract v1.0.1 (c) 2025 Raul Hussein (Astrocitas); PyQt6 port by
Adrian Knagg-Baugh. Reimplemented against polarispy: the PyQt6 GUI is replaced
by a polarispy.Dialog and pixels are read / written through polarispy. The HO
(Ha + OIII) extraction and the per-sensor QE table are preserved; the two-file
HO + SO tri-band combination of the original is not (yet) ported.
"""

import os

import polarispy

# Per-sensor quantum-efficiency coefficients (verbatim from DBXtract). They
# weight the R/G/B contributions when unmixing the broadcast bands.
SENSORS = {
    "IMX 571": {"r1": 0.02, "r2": 0.82, "r3": 0.75, "g1": 0.85, "g2": 0.08, "g3": 0.08, "b1": 0.50, "b2": 0.02, "b3": 0.03},
    "IMX 294": {"r1": 0.03, "r2": 0.65, "r3": 0.63, "g1": 0.92, "g2": 0.16, "g3": 0.18, "b1": 0.50, "b2": 0.05, "b3": 0.08},
    "IMX 533": {"r1": 0.03, "r2": 0.80, "r3": 0.73, "g1": 0.92, "g2": 0.16, "g3": 0.18, "b1": 0.50, "b2": 0.05, "b3": 0.08},
    "IMX 585": {"r1": 0.07, "r2": 1.00, "r3": 0.95, "g1": 0.80, "g2": 0.20, "g3": 0.24, "b1": 0.40, "b2": 0.05, "b3": 0.08},
    "IMX 183": {"r1": 0.05, "r2": 0.77, "r3": 0.68, "g1": 0.92, "g2": 0.15, "g3": 0.18, "b1": 0.45, "b2": 0.05, "b3": 0.08},
    "IMX 071": {"r1": 0.05, "r2": 0.75, "r3": 0.68, "g1": 0.70, "g2": 0.10, "g3": 0.13, "b1": 0.45, "b2": 0.03, "b3": 0.05},
    "IMX 410": {"r1": 0.08, "r2": 0.80, "r3": 0.75, "g1": 0.93, "g2": 0.15, "g3": 0.18, "b1": 0.45, "b2": 0.10, "b3": 0.12},
    "IMX 178": {"r1": 0.05, "r2": 0.78, "r3": 0.68, "g1": 0.93, "g2": 0.16, "g3": 0.18, "b1": 0.50, "b2": 0.05, "b3": 0.08},
    "IMX 455": {"r1": 0.03, "r2": 0.65, "r3": 0.58, "g1": 0.68, "g2": 0.06, "g3": 0.08, "b1": 0.40, "b2": 0.02, "b3": 0.03},
    "IMX 094": {"r1": 0.05, "r2": 0.80, "r3": 0.68, "g1": 0.68, "g2": 0.09, "g3": 0.11, "b1": 0.45, "b2": 0.02, "b3": 0.03},
    "IMX 462": {"r1": 0.05, "r2": 0.81, "r3": 0.79, "g1": 0.78, "g2": 0.25, "g3": 0.30, "b1": 0.40, "b2": 0.11, "b3": 0.15},
    "IMX 662": {"r1": 0.05, "r2": 0.88, "r3": 0.82, "g1": 0.92, "g2": 0.36, "g3": 0.35, "b1": 0.40, "b2": 0.05, "b3": 0.07},
}


def _extract_ho(rgb, coef):
    """DBXtract HO extraction: return (OIII, Ha) mono planes from an RGB stack."""
    import numpy as np
    r, g, b = rgb
    bg_r, bg_g, bg_b = np.median(r), np.median(g), np.median(b)
    r, g, b = r - bg_r, g - bg_g, b - bg_b

    cota = min(coef["g2"] / coef["r2"], 0.12)
    oiii_g = (g - cota * r) / (coef["g1"] - coef["g2"] * coef["r1"] / coef["r2"])
    oiii_b = (b - coef["b2"] * r / coef["r2"]) / (coef["b1"] - coef["b2"] * coef["r1"] / coef["r2"])
    oiii = ((2 * coef["g1"] * oiii_g) + (coef["b1"] * oiii_b)) / (2 * coef["g1"] + coef["b1"]) + max(bg_b, bg_g)
    ha = (r - coef["r1"] * (oiii - max(bg_b, bg_g))) / coef["r2"] + (bg_r + max(bg_b, bg_g))
    return oiii, ha


def main():
    poe = polarispy.connect()
    # Prefer the frame open in STUDIO; else the newest light.
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open a debayered colour image in STUDIO.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Dual-Band Extract")
    dlg.expects("linear")
    dlg.credits("DBXtract v1.0.1 (c) 2025 Raul Hussein (Astrocitas).\nPyQt6 port by Adrian Knagg-Baugh - GPL-3.0-or-later.\nPorted to polarispy.")
    dlg.info("Ha + OIII from: %s" % os.path.basename(path))
    dlg.select("sensor", "Camera sensor", list(SENSORS.keys()), default="IMX 571")

    _cache = {}

    def _preview(vals):
        if np is None:
            raise polarispy.PolarisError("Install the scripting runtime (Settings > Scripts) to preview.")
        if "rgb" not in _cache:
            rgb = poe.get_rgb()  # (H, W, 3), debayers a raw OSC mosaic
            if rgb is None:
                raise polarispy.PolarisError("no pixel data")
            a = np.moveaxis(rgb.astype("float32"), -1, 0)  # (3, H, W)
            h, w = a.shape[1:]
            step = max(1, int(max(h, w) / 500.0))
            _cache["rgb"] = a[:, ::step, ::step] if step > 1 else a
        oiii, ha = _extract_ho(_cache["rgb"], SENSORS[vals["sensor"]])
        return np.stack([ha, oiii, oiii], axis=0)  # HOO false colour (R=Ha, G/B=OIII)

    dlg.preview(_preview)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    try:
        import numpy as np
    except ImportError:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy. Install the scripting runtime in "
            "Settings > Scripts (the 'Install runtime' button).")

    coef = SENSORS[v["sensor"]]
    poe.update_progress("Reading pixels", 0.3)
    # get_rgb debayers a raw OSC mosaic (via BAYERPAT) or returns a colour frame.
    rgb = np.moveaxis(poe.get_rgb(), -1, 0).astype("float32")  # (3, H, W)

    poe.update_progress("Unmixing Ha / OIII", 0.6)
    oiii, ha = _extract_ho(rgb, coef)

    stem = os.path.splitext(path)[0]
    poe.update_progress("Writing Ha", 0.75)
    ha_path = poe.set_pixeldata(ha.astype("float32"), out_path=stem + "_Ha.fits", rescan=False)
    poe.log("Wrote: %s" % ha_path)
    poe.update_progress("Writing OIII", 0.9)
    oiii_path = poe.set_pixeldata(oiii.astype("float32"), out_path=stem + "_OIII.fits")
    poe.log("Wrote: %s" % oiii_path)

    poe.update_progress("Done", 1.0)
    poe.log("Finished. Ha and OIII appear in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
