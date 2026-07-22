# polaris: name=Blink Comparator; icon=🎞️; scope=any
"""Blink through the frames in the current STUDIO folder.

Cycles the folder's images in a play/pause player so you can spot movement
(asteroids, satellites), clouds, or bad subs by rapid alternation - the classic
blink comparator. Inspection only: nothing is written. Needs no extra Python
packages (the frames are auto-stretched and served by Polaris).

SPDX-License-Identifier: GPL-3.0-or-later
Inspired by the siril-scripts blink comparators. Reimplemented against
polarispy: a browser blink player fed by the folder listing.
"""

import polarispy


def main():
    poe = polarispy.connect()
    files = poe.list_dir()  # image files in the folder open in STUDIO Files
    if not files:
        poe.log("No image files in the current STUDIO folder.")
        poe.update_progress("Nothing to do", 1.0)
        return

    dlg = poe.dialog("Blink Comparator")
    dlg.credits("Blink Comparator - polarispy inspection tool, inspired by the\nsiril-scripts blink comparators. GPL-3.0-or-later.")
    dlg.info("%d frame(s) in %s" % (len(files), poe.cwd() or poe.home() or "the current folder"))
    dlg.blink([f["path"] for f in files])
    dlg.buttons(ok="Close", cancel="Close")
    dlg.run()  # blocks while the user blinks; inspection only
    poe.update_progress("Done", 1.0)
    poe.log("Blink closed.")


if __name__ == "__main__":
    main()
