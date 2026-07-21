# polaris: name=Folder Summary; icon=🗂️; scope=folder
"""Summarize the STUDIO library by type, filter and target (folder-scope demo).

A multi-file (folder) script: it does not touch a single open frame, it walks the
whole indexed library and reports counts. Needs no extra Python packages.
"""

from collections import Counter

import polarispy


def main():
    poe = polarispy.connect()
    poe.update_progress("Scanning the library", 0.3)
    frames = poe.list_frames(limit=2000)

    poe.log("Home: %s" % (poe.home() or "?"))
    poe.log("Frames indexed: %d" % len(frames))
    if not frames:
        poe.log("Nothing indexed. Capture some, or rescan STUDIO.")
        poe.update_progress("Empty", 1.0)
        return

    def counts(key):
        c = Counter((f.get(key) or "?") for f in frames)
        return ", ".join("%s=%d" % kv for kv in sorted(c.items(), key=lambda x: -x[1]))

    poe.log("By type:   " + counts("type"))
    poe.log("By filter: " + counts("filter"))
    poe.log("By target: " + counts("target")[:200])
    poe.update_progress("Done", 1.0)


if __name__ == "__main__":
    main()
