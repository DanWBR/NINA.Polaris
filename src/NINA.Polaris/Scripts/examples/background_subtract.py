# polaris: name=Background Subtraction; icon=🌌; scope=frame
"""Percentile background subtraction (polarispy pixel demo).

Needs numpy + astropy on the host Python. Loads the newest light frame, reads
its pixels as a numpy array, asks for a background percentile in a browser
dialog, subtracts it (clamped at zero), writes the result to a new FITS and
re-indexes STUDIO. Demonstrates the Phase 3 numpy pixel round-trip.
"""

import polarispy


def main():
    poe = polarispy.connect()
    # Prefer the frame the user has open in STUDIO; else the newest light.
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=1)
        if not frames:
            poe.log("No frame. Open one in STUDIO, or capture a light.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Background subtraction")
    dlg.expects("linear")
    dlg.credits("Background Subtraction - polarispy pixel demo.")
    dlg.info("Frame: %s" % path)
    dlg.slider("pct", "Background percentile", 0, 50, 10, step=1)
    values = dlg.run()
    if values is None:
        poe.log("Cancelled.")
        return

    poe.update_progress("Reading pixels", 0.3)
    try:
        import numpy as np
    except ImportError:
        raise polarispy.PolarisError(
            "This script needs numpy + astropy. Install the scripting runtime in "
            "Settings > Scripts (the 'Install runtime' button).")

    data = poe.get_pixeldata()
    if data is None:
        poe.log("The frame has no pixel data.")
        return
    bg = float(np.percentile(data, values["pct"]))
    poe.log("Subtracting background level %.5g (%.0f-th percentile)." % (bg, values["pct"]))
    poe.update_progress("Writing result", 0.7)
    out = np.clip(data.astype("float32") - bg, 0.0, None)
    written = poe.set_pixeldata(out)
    poe.log("Wrote: %s" % written)

    poe.update_progress("Done", 1.0)
    poe.log("Finished. The result will appear in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
