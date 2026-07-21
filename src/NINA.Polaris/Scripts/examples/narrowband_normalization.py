# polaris: name=Narrowband Normalization; icon=🎨; scope=frame
"""Channel normalization for narrowband palettes (NarrowbandNormalization port).

Normalizes the channels of a stretched SHO / HOO narrowband image so the
palette balances cleanly: per-channel boost, synthetic green for HOO, optional
SCNR, a LAB lightness source, and highlight / brightness tone shaping. Works on
a 3-channel (colour) image. Writes the result next to the source. Needs numpy +
astropy on the host (install the scripting runtime in Settings > Scripts).

SPDX-License-Identifier: GPL-3.0-or-later
Original: NarrowbandNormalization for Siril, (c) 2026 Yannick Dutertre (Cuiv),
built from Bill Blanshan & Mike Cranfield's math with permission. Reimplemented
against polarispy: the PyQt6 GUI is replaced by a polarispy.Dialog and the
pixels are read / written through polarispy. Core math preserved verbatim.
"""

import os

import polarispy

try:
    import numpy as np
except ImportError:
    np = None

_EPS = 1e-6


# ---- core math engine (verbatim from NarrowbandNormalization) --------------

def _mtf(m, x):
    x = np.asarray(x, dtype=np.float32)
    if abs(m - 0.5) < 1e-9:
        return x.copy()
    denom = (2.0 * m - 1.0) * x - m
    denom = np.where(np.abs(denom) < _EPS, np.copysign(_EPS, denom), denom)
    return ((m - 1.0) * x) / denom


def _rescale(x, lo, hi):
    if abs(hi - lo) < _EPS:
        return np.clip(x - lo, 0.0, 1.0)
    return np.clip((x - lo) / (hi - lo), 0.0, 1.0)


def _channel_stats(ch, blackpoint):
    mn = float(np.min(ch))
    med = float(np.median(ch))
    M = mn + blackpoint * (med - mn)
    mean = float(np.mean(ch, dtype=np.float64))
    adev = float(np.mean(np.abs(ch - mean), dtype=np.float64))
    E0 = adev / 1.2533 + mean - M
    return M, E0


def _boost_factor(a_target, a_ref, boost):
    denom = a_target - 2.0 * a_target * a_ref + a_ref
    if abs(denom) < 1e-9:
        denom = 1e-9
    return (a_target * (1.0 - a_ref) / denom) / boost


def _normalize_channel(ch, M_ch, strength):
    rescaled = _rescale(ch, M_ch, 1.0)
    stretched = _mtf(strength, rescaled)
    floor_part = np.minimum(ch, M_ch)
    out = 1.0 - (1.0 - stretched) * (1.0 - floor_part)
    return np.clip(out, 0.0, 1.0)


def _srgb_to_linear(c):
    c = np.clip(c, 0.0, None)
    return np.where(c > 0.04045, ((c + 0.055) / 1.055) ** 2.4, c / 12.92)


def _linear_to_srgb(c):
    c = np.clip(c, 0.0, None)
    return np.where(c > 0.0031308, 1.055 * (c ** (1.0 / 2.4)) - 0.055, 12.92 * c)


def _rgb_to_xyz(r, g, b):
    r1, g1, b1 = _srgb_to_linear(r), _srgb_to_linear(g), _srgb_to_linear(b)
    X = r1 * 0.4360747 + g1 * 0.3850649 + b1 * 0.1430804
    Y = r1 * 0.2225045 + g1 * 0.7168786 + b1 * 0.0606169
    Z = r1 * 0.0139322 + g1 * 0.0971045 + b1 * 0.7141733
    return X, Y, Z


def _f_lab(t):
    return np.where(t > 0.008856, np.cbrt(t), (7.787 * t) + 16.0 / 116.0)


def _f_lab_inv(t):
    return np.where(t > 0.206893, t ** 3, (t - 16.0 / 116.0) / 7.787)


def _xyz_to_lab(X, Y, Z):
    X1, Y1, Z1 = _f_lab(X), _f_lab(Y), _f_lab(Z)
    L = 116.0 * Y1 - 16.0
    a = 500.0 * (X1 - Y1)
    b = 200.0 * (Y1 - Z1)
    return L, a, b


def _xyz_to_rgb(X, Y, Z):
    R = X * 3.1338561 + Y * -1.6168667 + Z * -0.4906146
    G = X * -0.9787684 + Y * 1.9161415 + Z * 0.0334540
    B = X * 0.0719453 + Y * -0.2289914 + Z * 1.4052427
    return _linear_to_srgb(R), _linear_to_srgb(G), _linear_to_srgb(B)


def _cie_l_only(r, g, b):
    X, Y, Z = _rgb_to_xyz(r, g, b)
    L, _, _ = _xyz_to_lab(X, Y, Z)
    return (L + 16.0) / 116.0


def _synthetic_green(ha, oiii, mode, amount):
    amount = float(np.clip(amount, 0.0, 1.0))
    if mode == "Mode 1":
        g = amount * ha + (1.0 - amount) * oiii
    elif mode == "Mode 2":
        g = (np.clip(ha, 0, 1) ** amount) * (np.clip(oiii, 0, 1) ** (1.0 - amount))
    else:
        g = 1.0 - (1.0 - amount * ha) * (1.0 - (1.0 - amount) * oiii)
    return np.clip(g, 0.0, 1.0)


def _highlight_reduction(x, hl_reduction):
    hl_reduction = max(hl_reduction, 1e-3)
    m = 1.0 - 0.5 / hl_reduction
    term_a = _mtf(m, x) * x
    term_b = x * (1.0 - x)
    return term_a + term_b


def _brightness_stretch(x, brightness):
    brightness = max(brightness, 1e-3)
    return _mtf(1.0 / brightness * 0.5, x)


PALETTE_SLOTS = {
    "HOO": {"Ha": 0, "OIII": 2},
    "SHO": {"SII": 0, "Ha": 1, "OIII": 2},
    "HSO": {"Ha": 0, "SII": 1, "OIII": 2},
    "HOS": {"Ha": 0, "OIII": 1, "SII": 2},
}


def process_image(data, params):
    """data: (H, W, 3) float in [0, 1]. returns (H, W, 3) float in [0, 1]."""
    palette = params["palette"]
    slots = PALETTE_SLOTS[palette]
    data = np.asarray(data, dtype=np.float32)

    ha = data[:, :, slots["Ha"]]
    oiii = data[:, :, slots["OIII"]]
    sii = data[:, :, slots["SII"]] if "SII" in slots else None

    blackpoint = params["shadow_point"]
    M_ha, E0_ha = _channel_stats(ha, blackpoint)
    M_o, E0_o = _channel_stats(oiii, blackpoint)
    ref_denom = 1.0 - M_o
    if abs(ref_denom) < 1e-9:
        ref_denom = 1e-9
    A0_ha = E0_ha / ref_denom
    A0_o = E0_o / ref_denom

    E1 = _boost_factor(A0_o, A0_ha, params["oiii_boost"])
    oiii_norm = _normalize_channel(oiii, M_o, E1)

    if sii is not None:
        M_s, E0_s = _channel_stats(sii, blackpoint)
        A0_s = E0_s / ref_denom
        E4 = _boost_factor(A0_s, A0_ha, params["sii_boost"])
        sii_norm = _normalize_channel(sii, M_s, E4)
    else:
        sii_norm = None

    out = np.empty_like(data)
    out[:, :, slots["Ha"]] = ha
    out[:, :, slots["OIII"]] = oiii_norm
    if sii is not None:
        out[:, :, slots["SII"]] = sii_norm
    else:
        green = _synthetic_green(ha, oiii_norm, params["blend_mode"], params["blend_amount"])
        out[:, :, 1] = green

    if sii is not None:
        scnr_amt = float(np.clip(params["scnr"], 0.0, 1.0))
        if scnr_amt > 0.0:
            r_ch, g_ch, b_ch = out[:, :, 0], out[:, :, 1], out[:, :, 2]
            reduced = np.minimum(np.mean(np.stack([r_ch, b_ch]), axis=0), g_ch)
            out[:, :, 1] = (1.0 - scnr_amt) * g_ch + scnr_amt * reduced

    lightness = params["lightness"]
    if lightness != "Off":
        r, g, b = out[:, :, 0], out[:, :, 1], out[:, :, 2]
        X, Y, Z = _rgb_to_xyz(r, g, b)
        _, a, bb = _xyz_to_lab(X, Y, Z)
        del X, Y, Z

        if lightness == "Original":
            Y2 = _cie_l_only(data[:, :, 0], data[:, :, 1], data[:, :, 2])
        elif lightness == "Ha":
            Y2 = (ha + 0.16) / 1.16
        elif lightness == "SII" and sii is not None:
            Y2 = (sii + 0.16) / 1.16
        else:
            Y2 = (oiii + 0.16) / 1.16

        X2 = (a / 500.0) + Y2
        Z2 = Y2 - (bb / 200.0)
        X3, Y3, Z3 = _f_lab_inv(X2), _f_lab_inv(Y2), _f_lab_inv(Z2)
        r3, g3, b3 = _xyz_to_rgb(X3, Y3, Z3)
        out[:, :, 0] = np.clip(r3, 0.0, 1.0)
        out[:, :, 1] = np.clip(g3, 0.0, 1.0)
        out[:, :, 2] = np.clip(b3, 0.0, 1.0)

    out = _highlight_reduction(out, params["highlight_reduction"])
    out = _brightness_stretch(out, params["brightness"])
    out = _rescale(out, 0.0, 1.0)
    return out.astype(np.float32)


def _to_hwc(arr):
    """Normalize (3,H,W) or (H,W,3) to (H,W,3); reject mono."""
    if arr.ndim == 3 and arr.shape[0] == 3:
        return np.moveaxis(arr, 0, -1)
    if arr.ndim == 3 and arr.shape[-1] == 3:
        return arr
    raise polarispy.PolarisError(
        "Narrowband Normalization needs a 3-channel (SHO/HOO) colour image.")


def main():
    poe = polarispy.connect()
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open a stretched SHO/HOO colour image in STUDIO.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Narrowband Normalization")
    dlg.info("Stretched SHO / HOO image: %s" % os.path.basename(path))
    dlg.select("palette", "Palette", ["HOO", "SHO", "HSO", "HOS"], default="HOO")
    dlg.select("lightness", "Lightness source", ["Off", "Original", "Ha", "SII", "OIII"], default="Off")
    dlg.select("blend_mode", "Synthetic green (HOO)", ["Mode 1", "Mode 2", "Mode 3"], default="Mode 1")
    dlg.slider("blend_amount", "Green blend amount", 0.0, 1.0, 0.6, step=0.01)
    dlg.slider("scnr", "SCNR (SHO)", 0.0, 1.0, 0.0, step=0.01)
    dlg.slider("oiii_boost", "OIII boost", 0.2, 3.0, 1.0, step=0.05)
    dlg.slider("sii_boost", "SII boost", 0.2, 3.0, 1.0, step=0.05)
    dlg.slider("shadow_point", "Shadow point", 0.0, 1.0, 1.0, step=0.01)
    dlg.slider("highlight_reduction", "Highlight reduction", 0.2, 3.0, 1.0, step=0.05)
    dlg.slider("brightness", "Brightness", 0.2, 3.0, 1.0, step=0.05)
    v = dlg.run()
    if v is None:
        poe.log("Cancelled.")
        return

    if np is None:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy. Install the scripting runtime in "
            "Settings > Scripts (the 'Install runtime' button).")

    poe.update_progress("Reading pixels", 0.3)
    data = poe.get_pixeldata()
    if data is None:
        poe.log("The frame has no pixel data.")
        return
    arr = data.astype(np.float32)
    # The math expects stretched data in [0, 1]; normalize raw ADU if needed.
    mx = float(np.nanmax(arr)) if arr.size else 1.0
    white = float(np.iinfo(data.dtype).max) if np.issubdtype(data.dtype, np.integer) else (mx if mx > 1.5 else 1.0)
    hwc = _to_hwc(arr / white)

    poe.update_progress("Normalizing", 0.6)
    out_hwc = process_image(hwc, {k: v[k] for k in (
        "palette", "lightness", "blend_mode", "blend_amount", "scnr",
        "oiii_boost", "sii_boost", "shadow_point", "highlight_reduction", "brightness")})

    # Back to the source layout for writing.
    out = np.moveaxis(out_hwc, -1, 0) if data.ndim == 3 and data.shape[0] == 3 else out_hwc
    poe.update_progress("Writing result", 0.9)
    written = poe.set_pixeldata(out.astype("float32"),
                                out_path=os.path.splitext(path)[0] + "_nbnorm.fits")
    poe.log("Wrote: %s" % written)
    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result appears in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
