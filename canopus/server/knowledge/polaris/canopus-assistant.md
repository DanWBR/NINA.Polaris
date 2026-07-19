# Canopus Assistant: backends and requirements

Canopus is the optional AI assistant built into Polaris. It plans the night, reads
rig state, drives the rig with your approval, inspects frames (cloud only), and
answers astrophotography questions, right inside the Polaris web UI. It is opt-in
and off by default: the open-source Polaris ships a neutral, inactive host, and the
assistant appears only if you enable it in Settings, Assistant.

However it is powered, the safety model is the same. Anything that moves hardware or
changes a running session (slew, autofocus, start or stop a capture, dither) is
proposed as a plan that you approve, review, or reject first. The assistant never
acts on the rig without your confirmation.

## Choosing a backend

Canopus is one assistant with a choice of "brain". Pick the backend in Settings,
Assistant. All three drive the rig the same way, through your browser, so the rest
of the experience is identical. Your browser is always the bridge: it executes the
approved action on your local Polaris over the LAN and streams status back, so there
is no inbound connection to your rig.

### Cloud

The most capable option, always on, with nothing to install. The AI runs on our
servers and returns a reply.

- Cost: a paid subscription, US$4.99 per month, with a 7-day free trial.
- Runs on: our servers (Microsoft Azure). No local model and no GPU are needed.
- Minimum: an internet connection and a subscription. Any device that runs the
  Polaris web UI works.
- Recommended: the same. This is the strongest option, and the only one that does
  frame analysis (looking at your images).
- Privacy: your messages are processed to generate a reply and then discarded, and
  are not used to train models. Only your email, a login token, and your
  subscription status are stored. Payment is handled by Stripe.

### On this server

Free and fully offline. A small language model runs on the Polaris host itself, so
any phone or tablet on your network becomes a thin client. No account, no
subscription.

- Cost: free.
- Runs on: the Polaris host, on the CPU. An NPU or GPU is a bonus, not a
  requirement.
- Minimum: a 64-bit host with about 6 GB of RAM free alongside Polaris.
- Recommended: 8 GB of RAM or more and a faster processor. Warm replies land in a
  few seconds; the first reply of a session takes longer while the model warms up
  and reads the tool catalog.
- Text-only: it plans the night, drives the rig, and answers questions. Frame
  analysis (vision) is cloud-only.
- Privacy: everything stays on your hardware. Nothing is sent to us or to any AI
  provider.

### On this device

Free and offline, running the model on the machine you are using.

- Cost: free.
- Runs on: your computer's GPU through a local LLM server you run (Ollama, LM
  Studio, or llama.cpp), or your phone in the Polaris mobile app.
- Minimum: on a desktop, a local LLM server and a tool-capable model that fits your
  GPU memory. On mobile, an 8 GB device, because the model loads fully into RAM and
  a smaller device is killed by the operating system.
- Recommended: on a desktop, a 12 to 24 billion parameter tool-capable model on a
  16 GB or larger GPU. On mobile, an 8 GB phone or tablet, for smooth replies in a
  few seconds.
- Text-only: same as the on-server backend. Frame analysis is cloud-only.
- Privacy: the model runs locally and nothing leaves your machine. If you point the
  desktop option at a remote, non-local endpoint, that service sees your messages
  under its own terms.

## Which one should I use?

- Want the best answers and image analysis, and do not mind a subscription: cloud.
- Want a free, private assistant and your Polaris host has 8 GB or more: on this
  server.
- Have a powerful PC, Mac, or a recent phone and want it free and private: on this
  device.

The on-server and on-device backends are text-only and skip frame analysis; the
cloud backend is the only one that inspects your images.
