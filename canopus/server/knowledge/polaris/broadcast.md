# Live broadcast (YouTube, Twitch, Facebook, Instagram)

Polaris can stream the session straight from the host, with no computer
at the scope and nothing running on your laptop. The host composes a
video frame of its own (the picture, an object card, a line of session
numbers) and publishes it over RTMP.

Settings -> Network & security -> Live broadcast.

**Nothing reaches the internet until you press Start.** There is no
auto-start and nothing resumes after a restart of the host.

## What is on screen

```
+------------------------------------------------------+
|  Polaris Live Stream  ·  Quintal          <- header   |
|  550 mm f/5.5 · ASI2600MC Pro · AM5 · PHD2            |
|                                        +------------+ |
|                                        | cutout     | |
|              the picture               | Orion Neb. | |
|                                        | M42 · NGC  | |
|                                        | facts      | |
|                                        | description| |
|                                        +------------+ |
|  M42 · L 120s g100 · 14 frames · 28 min · SNR 31.4    |
+------------------------------------------------------+
```

- **Header**, top: the broadcast title you set, the rig name, and the
  equipment. Static for the whole session, which is why it is at the
  top and not mixed in with the numbers that move.
- **Object card**, right: a cutout of the target, its common name,
  catalogue ids, facts and a short description.
- **Banner**, bottom: target, filter, exposure, gain, frames,
  integration, SNR, guide RMS and sensor temperature. Anything not
  measured is left out rather than shown as a zero.

Each of the three can be turned off.

## The picture

Whatever Polaris is looking at, in this order, re-checked twice a
second:

1. the video stream, if one is running (planetary work);
2. otherwise the last image the relay saw, which covers LIVE, the
   stack, PREVIEW and AUTORUN, and works with no browser attached;
3. otherwise the last frame drawn, so the picture holds instead of
   going black between subs.

The broadcast never starts a camera stream of its own, because a
stream holds the camera exclusively and would block every exposure for
as long as the broadcast ran. It only joins a stream someone else
started.

It also never waits. A deep sky sub takes minutes and a plate solve
takes seconds; a broadcast that paused for those would stop every few
minutes.

## Setting it up

| Platform | Where the URL and key come from |
|---|---|
| YouTube | Studio -> Go live -> Stream settings. The ingest URL is prefilled. |
| Twitch | Creator Dashboard -> Settings -> Stream. Use a regional ingest server if the default is slow. |
| Facebook | Live producer -> Streaming software. RTMPS only, which is what is prefilled. |
| Instagram | Live producer, professional account. The URL and key are issued per session, so paste both. |
| Custom | Any RTMP or RTMPS server, including one of your own. |

Picking a platform only fills the URL field in. What you save is what
gets used, so a regional Twitch server or a per-session Instagram URL
simply replaces it.

The **stream key is write-only**. It is stored in
`{DataDir}/broadcast/config.json`, mode 0600 in a 0700 directory, and
is never returned to a browser: the card only knows whether one is
set. It is deliberately not on the profile, because
`GET /api/system/profile` hands the profile to any client that asks,
and a stream key is enough to broadcast to your channel as you.

If you paste the address of the studio page instead of the ingest URL,
Polaris refuses it and says so. That is the commonest mistake.

## Quality

| Setting | Resolution | Bitrate | Roughly |
|---|---|---|---|
| Low | 854x480 | 1000 kbps | 0.5 GB per hour |
| Medium | 1280x720 | 2500 kbps | 1.1 GB per hour |
| High | 1920x1080 | 4500 kbps | 2.0 GB per hour |

That figure is both what goes up your link and what a recording costs
on disk. Broadcasting from a phone hotspot shares the link with your
own browser session, so start at Low there.

## Recording

**Also record to a file** keeps the same composed video as an MP4
under the capture folder, in `broadcast/`. It works with or without a
destination: composing and keeping the file is a complete use of the
feature when the uplink is bad or absent.

The file is written in fragments, so a power cut, an out-of-memory
kill or a crash costs only the last few seconds rather than the whole
night. A plain MP4 keeps its index in memory until the writer exits,
which is why it is not used here.

## Descriptions

The text on the object card is taken in a fixed order:

1. the text Polaris ships, for the Messier catalogue and the well
   known NGC, IC and Sharpless objects;
2. a description fetched once from Wikipedia and cached, in your
   interface language, if **Look up descriptions online** is on;
3. a sentence built from the catalogue facts.

The third is the floor, so the card is never blank. A lookup never
delays a frame: the card goes on air with whatever is on disk and
improves itself if and when the fetch lands. With no network nothing
waits and nothing fails.

Fetched text carries a small "via Wikipedia" credit, because it is
published under a licence that requires attribution and a broadcast is
publishing.

## Encoders

The broadcast needs `ffmpeg`, which is a recommended and not a
required dependency of the `.deb`, so it can be missing. The card then
shows every path it looked in, and a button to look again after you
install it.

Polaris picks the H.264 encoder by **trying** it, not by reading a
list. Every ffmpeg build reports every encoder it was compiled with,
and whether the hardware behind one is actually present and usable is
a different question: a machine that lists `h264_qsv` with no working
Quick Sync answers "Could not open encoder before EOF" on the first
frame. So each candidate encodes a fifth of a second of black before
it is trusted, which costs about a second at start and settles it.

Preference is by architecture: on ARM boards `h264_rkmpp` (Rockchip,
so Orange Pi 5 and Radxa) then `h264_v4l2m2m` (the kernel path most
ARM boards expose); on x86 `h264_qsv`, `h264_nvenc`, `h264_vaapi`.
Software `libx264` is the floor everywhere.

The status block reports which one is in use and whether it is
hardware. A Pi 4 encoding 1080p in software while it captures will not
keep up; use Low there, or a board with a hardware encoder.

## While it runs

An **ON AIR** chip sits on the activity bar on every tab, and turns
amber once the encoder has had to be restarted. The card shows uptime,
bitrate, the encoder, dropped frames and reconnects.

RTMP connections drop, and on a hotspot at a dark site that is a
normal night rather than a fault. When ffmpeg exits while you still
want a broadcast, Polaris restarts it with a backoff capped at thirty
seconds and counts the reconnect. Reconnects climbing slowly are the
uplink; climbing fast, with an error beside them, is worth reading.

**Stop** lets ffmpeg finish: it flushes, closes the recording properly
and exits on its own. Killing it instead is what leaves an unplayable
file, so the stop is allowed ten seconds before the process is killed.

## Limits, stated up front

- **No audio.** A silent AAC track is sent because YouTube refuses a
  video-only stream. Commentary would mean a microphone on the host.
- **Instagram** caps a session at about an hour. That is Instagram,
  not Polaris.
- **The frame rate into the encoder is two per second.** The picture
  changes once per sub, so this is plenty for deep sky; it is low for
  planetary, where the video stream is moving faster than that.
- **Object types and constellation names** on the card come from the
  catalogue and stay in English, even when the rest of the card is in
  your language.

## Endpoints

| Method | Path | What |
|---|---|---|
| GET | `/api/broadcast/config` | everything except the key, plus `hasStreamKey` |
| PUT | `/api/broadcast/config` | null keeps a field, `""` clears the key |
| GET | `/api/broadcast/status` | the same block `/ws/status` pushes |
| POST | `/api/broadcast/start` | 400 with a reason when it cannot |
| POST | `/api/broadcast/stop` | graceful |
| GET | `/api/broadcast/encoders` | what ffmpeg can do, and where it was found |
| POST | `/api/broadcast/rescan` | look for ffmpeg again after installing it |

See also: [Storage push](cloud-storage.md) for getting the frames
themselves off the host, and [Relay](relay.md) for reaching the
Polaris interface from outside your LAN.
