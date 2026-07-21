"""GHS-stretch the newest light frame (polarispy demo).

Lists the STUDIO light frames, applies an auto GHS stretch to the first one,
and reports progress and log lines back to the Polaris UI. Headless (no UI):
this is the Phase 1 example that proves the polarispy round-trip.
"""

import polarispy


def main():
    poe = polarispy.connect()
    poe.log("Connected to Polaris.")
    poe.update_progress("Listing light frames", 0.1)

    frames = poe.list_frames(type="LIGHT", limit=50)
    poe.log("Found %d light frame(s) in the library." % len(frames))
    if not frames:
        poe.log("No light frames yet. Capture some (or add + rescan in STUDIO) and run again.")
        poe.update_progress("Nothing to do", 1.0)
        return

    first = frames[0]
    path = first.get("path") or first.get("Path")
    if not path:
        poe.log("The first frame has no path field; aborting.")
        poe.update_progress("Error", 1.0)
        return

    poe.log("Applying an auto GHS stretch to: %s" % path)
    poe.update_progress("Stretching", 0.5)
    result = poe.stretch(path, mode="ghs", auto=True)

    for ok in result.get("results", []):
        poe.log("Wrote: %s" % ok.get("outputPath"))
    for bad in result.get("failures", []):
        poe.log("Failed: %s" % bad.get("error"))

    poe.update_progress("Done", 1.0)
    poe.log("Script finished. The result will appear in STUDIO after the rescan.")


if __name__ == "__main__":
    main()
