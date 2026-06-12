# Screenshots

Each feature row on the landing page pairs its text with a screenshot. Drop the
real UI captures here using the filenames below. Until a file exists, that
feature shows a styled placeholder ("screenshot coming soon"), so the page
never looks broken.

## Expected filenames

The mapping lives in [`content/pages/home.json`](../../../content/pages/home.json)
(`features[].image`). Current slots:

| File                | Feature                                   |
|---------------------|-------------------------------------------|
| `devices.png`       | Connect any rig (INDI / ASCOM / Alpaca)   |
| `guiding.png`       | Native PHD2 guiding                       |
| `sky-explorer.png`  | Sky explorer with slew & center           |
| `live-view.png`     | Complete live stacking panel              |
| `video.png`         | Video recording + processing              |
| `sequencer.png`     | Advanced sequencer                        |
| `autofocus.png`     | Auto focus                                |
| `studio.png`        | STUDIO: stacking + AI post-processing     |
| `opencl.png`        | GPU acceleration via OpenCL               |

### Getting Started page (`content/guide/getting-started.json`, `steps[].image`)

| File               | Step                          |
|--------------------|-------------------------------|
| `gs-rigs.png`      | Set up your rig               |
| `gs-connect.png`   | Connect your equipment        |
| `gs-guiding.png`   | Set up guiding                |
| `gs-focus.png`     | Get sharp focus               |
| `gs-sky.png`       | Frame your target             |
| `gs-live.png`      | Capture (live / autorun)      |
| `gs-studio.png`    | Stack and edit in STUDIO      |

## Tips

- **Aspect ratio:** the feature media is **16:10** (`object-fit: cover`).
  Capture at ~1600×1000 for the cleanest result.
- **Format:** PNG for crisp UI text; JPG/WebP fine for photo-heavy shots.
- You can also set the image visually in the Tina editor (`/admin`): the
  "Screenshot" field on each feature opens the media picker.
- To add/rename/reorder features, edit `content/pages/home.json` (or use Tina).
