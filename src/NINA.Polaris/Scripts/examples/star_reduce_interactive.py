# polaris: name=Star Reduction; icon=✨; scope=frame
"""Interactive star reduction (polarispy dialog demo).

Lists the STUDIO light frames, asks the user for the reduction settings through
a Polaris browser dialog, then runs star reduction on the newest frame. Shows
the Phase 2 declarative UI: the dialog is defined in Python but rendered and
answered in the browser.
"""

import polarispy


def main():
    poe = polarispy.connect()
    poe.update_progress("Choosing a frame", 0.1)

    # Prefer the frame open in STUDIO; else the newest light.
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=50)
        poe.log("No open frame; found %d light frame(s)." % len(frames))
        if not frames:
            poe.log("Nothing to do. Open a frame in STUDIO, or capture some lights.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")
        poe.load(path)

    dlg = poe.dialog("Star reduction")
    dlg.expects("stretched")
    dlg.credits("Star Reduction - polarispy dialog demo.\nRuns Polaris's star-reduction processing.")
    dlg.info("Frame: %s" % path)
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
        poe.output(ok.get("outputPath"))
    for bad in result.get("failures", []):
        poe.log("Failed: %s" % bad.get("error"))

    poe.update_progress("Done", 1.0)
    poe.log("Finished.")


if __name__ == "__main__":
    main()
