# Screenshots

Drop real UI captures here to populate the "See it in the browser" gallery on
the landing page. Until a file exists, that card shows a styled placeholder
("screenshot coming soon") — the page never looks broken.

## Expected filenames

The gallery is defined in [`src/data/site.ts`](../../../src/data/site.ts)
(`screenshots` array). The current slots are:

| File             | Card                  |
|------------------|-----------------------|
| `sequencer.png`  | Advanced Sequencer    |
| `live-view.png`  | Live view & stacking  |
| `sky-map.png`    | Sky map & atlas       |
| `guiding.png`    | PHD2 guiding          |
| `rigs.png`       | Equipment rigs        |
| `focus.png`      | Auto-focus (V-curve)  |

## Tips

- **Aspect ratio:** cards crop to **16:10** (`object-fit: cover`). Capture at
  ~1600×1000 (or any 16:10-ish size) for the cleanest result.
- **Format:** PNG for crisp UI text; JPG/WebP are fine for photo-heavy shots.
- To add/rename/reorder cards, edit the `screenshots` array in `site.ts` —
  no component changes needed.
