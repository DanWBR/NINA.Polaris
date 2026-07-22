# polaris: name=GHS Stretch; icon=🌗; scope=frame
"""GHS-stretch the newest light frame (polarispy demo).

Lists the STUDIO light frames, applies an auto GHS stretch to the first one,
and reports progress and log lines back to the Polaris UI. Headless (no UI):
this is the Phase 1 example that proves the polarispy round-trip.
"""

import polarispy


def main():
    poe = polarispy.connect()
    poe.log("Connected to Polaris.")
    poe.update_progress("Choosing a frame", 0.1)

    # Prefer the frame the user had open in STUDIO; else the newest light.
    path = poe.current
    if not path:
        frames = poe.list_frames(type="LIGHT", limit=50)
        poe.log("No open frame; found %d light frame(s)." % len(frames))
        if not frames:
            poe.log("Nothing to do. Open a frame in STUDIO, or capture some lights.")
            poe.update_progress("Nothing to do", 1.0)
            return
        path = frames[0].get("path") or frames[0].get("Path")

    poe.log("Applying an auto GHS stretch to: %s" % path)
    poe.update_progress("Stretching", 0.5)
    result = poe.stretch(path, mode="ghs", auto=True)

    for ok in result.get("results", []):
        poe.log("Wrote: %s" % ok.get("outputPath"))
        poe.output(ok.get("outputPath"))
    for bad in result.get("failures", []):
        poe.log("Failed: %s" % bad.get("error"))

    poe.update_progress("Done", 1.0)
    poe.log("Script finished. The result will appear in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
