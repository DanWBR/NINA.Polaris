"""Interactive star reduction (polarispy dialog demo).

Lists the STUDIO light frames, asks the user for the reduction settings through
a Polaris browser dialog, then runs star reduction on the newest frame. Shows
the Phase 2 declarative UI: the dialog is defined in Python but rendered and
answered in the browser.
"""

import polarispy


def main():
    poe = polarispy.connect()
    poe.update_progress("Listing light frames", 0.1)
    frames = poe.list_frames(type="LIGHT", limit=50)
    poe.log("Found %d light frame(s)." % len(frames))
    if not frames:
        poe.log("No light frames yet. Capture some (or rescan STUDIO) and run again.")
        poe.update_progress("Nothing to do", 1.0)
        return

    path = frames[0].get("path") or frames[0].get("Path")

    dlg = poe.dialog("Star reduction")
    dlg.info("Target: %s" % (frames[0].get("target") or path))
    dlg.slider("amount", "Amount", 0.0, 1.0, 0.5, step=0.05)
    dlg.number("iterations", "Iterations", default=1, min=1, max=5, step=1)
    dlg.checkbox("open_after", "Refresh STUDIO when done", True)
    dlg.buttons(ok="Reduce", cancel="Cancel")

    values = dlg.run()
    if values is None:
        poe.log("Cancelled by the user.")
        poe.update_progress("Cancelled", 1.0)
        return

    poe.log("Reducing stars: amount=%s iterations=%s" % (values["amount"], values["iterations"]))
    poe.update_progress("Reducing stars", 0.6)
    result = poe.star_reduce(path, amount=values["amount"], iterations=int(values["iterations"]))

    for ok in result.get("results", []):
        poe.log("Wrote: %s" % ok.get("outputPath"))
    for bad in result.get("failures", []):
        poe.log("Failed: %s" % bad.get("error"))

    poe.update_progress("Done", 1.0)
    poe.log("Finished.")


if __name__ == "__main__":
    main()
