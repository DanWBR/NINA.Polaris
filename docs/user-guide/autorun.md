# AUTORUN tab (simple sequence)

Polaris has two sequencers:

1. **AUTORUN** (this tab) — simple flat list of `(target, filter,
   exposure, count)` items. Quick to set up, no triggers, no branches.
2. **ADV** — tree-based Advanced Sequencer with containers, conditions,
   triggers (see [ADV guide](adv-sequencer.md)). For complex multi-
   target unattended runs.

Use AUTORUN for "I want 100 frames of M81 in L tonight". Use ADV for
"M81 in LRGB with auto-flip + dither every 3 + AF on temperature
change + safe shutdown if cloud cover exceeds 40%".

## Adding targets

Click **+ Add target** — a new row appears with:

- **Name** — usually pre-filled from your SKY tab last-searched target
- **RA / Dec** — coordinates
- **Filter** — dropdown when filter wheel connected
- **Exposure (s)** — per-frame
- **Count** — number of frames to capture
- **Gain / Offset / Bin** — overrides per row (optional)

You can stack multiple targets vertically + the engine runs them in
order, slewing + plate-solve-centering between each.

## Running

- **▶ Start sequence** — runs from the first row
- **⏸ Pause / ▶ Resume** — suspends after the current frame finishes
- **⏹ Stop** — aborts immediately + restores tracking to last known good

While running:

- **Progress bars** per row (frames done / total)
- **Total stats** at top (elapsed / remaining / frames captured)
- **Last error** displayed if anything aborted
- **Current frame** indicator with countdown

## End-actions (collapsible panel)

What happens when the sequence completes:

- **Park mount** — slew to park position + disable tracking
- **Warm camera** — gradual cooler ramp-up to ambient (protects sensor)
- **Stop PHD2** — gracefully disconnect equipment + stop guiding
- **Auto-GraXpert BGE** — fire-and-forget background extraction on
  every captured light (`{rig}/bge/{target}/`)
- **Shutdown server** — `systemctl poweroff` after a delay (Linux only,
  needs sudoers)

## Dithering panel (collapsible)

- **Enabled** — master toggle
- **Pixels** — dither amplitude (5px typical)
- **Every N frames** — cadence (default 1 = every frame)
- **RA only** — for mounts with poor Dec correction
- **Settle pixels / time / timeout** — passed to PHD2

Dither fires via PHD2's `dither` JSON-RPC + waits for `SettleDone`
event before the next frame starts. Silently skipped when PHD2 isn't
guiding.

## Meridian flip panel (collapsible)

- **Enabled** — master toggle
- **Minutes after meridian** — when to trigger (default 5 min)
- **Pause before flip** (s) — buffer for settle
- **Auto-focus after flip** — optional AF run before resuming

Workflow when triggered:

1. Pause guiding
2. Stop tracking (optional)
3. Slew back to target (most mounts flip on slew-cross-meridian)
4. Plate-solve + re-center
5. (Optional) Auto-focus
6. Resume guiding (auto-recalibrate if XAngle changed)
7. Resume sequence

Status banner shows phase live: "Slewing → Centering → Resuming"...

## Sequence status WebSocket payload

The 1Hz `/ws/status` stream carries:

```jsonc
{
  "sequence": {
    "state": "running",
    "currentItemIndex": 2,
    "currentFrameInItem": 5,
    "totalFrames": 30,
    "totalFramesCompleted": 7,
    "elapsedSeconds": 410,
    "estimatedRemainingSeconds": 1380,
    "dither": { "issued": 3, "framesSinceLast": 0 }
  }
}
```

## Common pitfalls

**Sequence starts but first frame fails** — usually the target hasn't
risen yet or is below 0° altitude. Check the SKY tab altitude chart
before queuing.

**Plate solve fails after slew** — see
[Slew & Center troubleshooting](sky-explorer.md#troubleshooting-slew--center).

**Dither doesn't fire** — PHD2 not guiding (check the GUIDE tab
status). The sequence engine silently skips dither when PHD2 isn't
ready; toast warning surfaces it.

**Meridian flip hangs at "Slewing"** — your mount may not auto-flip
on slew-back-to-target. Some mounts need an explicit pier-side
command. File an issue with your mount model.

## See also

- [ADV (Advanced Sequencer)](adv-sequencer.md) for branching / triggers
- [GUIDE → Dither](guide-phd2.md)
- [FOCUS → AF triggers](focus.md#auto-focus-triggers-advanced)
- [Glossary → Meridian / Dither](GLOSSARY.md#m)
