# Canopus Assistant

Everything else in this handbook is free and open source. **Canopus
Assistant** is an optional AI companion that rides inside the Polaris web
interface. It comes in four flavours: one paid cloud tier, whose
subscription revenue funds the ongoing development of Polaris itself; two
free tiers that run entirely on your own hardware; and one that points
Polaris at a commercial AI provider with an API key of your own, billed by
that provider rather than by us. All four are strictly opt-in. Polaris ships with the assistant switched off, never
depends on it, and works exactly the same whether or not you ever turn it
on. This chapter explains what it does, how to choose and enable a
backend, and, just as importantly, what it does *not* do with your data or
your telescope.

Think of it as a co-pilot rather than an autopilot. It plans the night,
proposes the actions to get there, answers astrophotography questions in
plain language, and (on the cloud tier) inspects your frames. Anything
that moves hardware or changes a running session waits for your approval
first.

## Canopus Assistant / What it can do

Every backend shares the same core jobs. Some need either a vision-capable
model or more horsepower than a small local model has; those are called
out below. Where this chapter says "cloud tier" it means the subscription
one, and where a capability needs a frontier model, the bring-your-own-key
backend reaches it too.

**It plans the night.** Tell it what you feel like shooting, "a nebula
that's up now", "something for a two-hour window", "narrowband from a
Bortle 7 backyard", and it ranks well-placed deep-sky targets (plus the
Moon and planets), checks their altitude across the night, the weather and
the Moon's interference, and then assembles a ready-to-run plan in the
night planner (the "The Multi-Target Night Planner" chapter) with sensible exposures, dithering, and
refocus points. You review and tweak it before anything runs. (Building
the full multi-target plan is a cloud-tier feature; the local tiers still
suggest targets, exposures and framing.)

**It drives the rig, with your approval.** Slewing to a target,
autofocusing (the "Focusing" chapter), starting guiding, a sequence or a live
stack, dithering, taking a test frame, cooling the camera, changing the
filter, nudging the focuser, and opening or closing the dome or roof are
each proposed as an action you **approve**, **review**, or **reject**.
When it acts, it navigates the Polaris UI to the relevant panel so you can
watch focus, guiding (the "Autoguiding" chapter), or the stack as it works. If you
mention rain or clouds coming, it proposes to close the roof right away,
and on the cloud tier to park the mount as well. (The local tiers can slew, autofocus, start guiding and
a live stack, dither, take a test frame and close the roof; the other
actions are cloud-tier extras.)

**It knows astrophotography.** Grounded, practical answers on focusing,
guiding, exposure and histograms, filters, polar alignment
(the "Polar Alignment" chapter), calibration frames, and diagnosing bad subs,
drawing on a knowledge base that includes this very handbook. Available on
every backend.

**It looks at your images (cloud tier, or your own key).** Ask "is this
frame any good?" and it inspects the current frame, focus and star shape,
trailing, gradients, framing, and tells you what to fix. It can also grade
a folder of subs and drive the STUDIO stacking and post-processing tools
(the "STUDIO: Calibration and Stacking" chapter) on your behalf, again as reviewable steps. Frame analysis
needs a vision-capable model: the subscription tier has one, the
bring-your-own-key backend has one as long as the model you name can see
images, and the two local tiers are text only.

## Canopus Assistant / Tapping instead of typing

At the telescope you often hold a tablet in cold hands, so the chat offers
tappable badges (rounded buttons) above the text field. When you tap one,
Polaris sends it exactly as if you had typed it. The text field always
stays there, so you can type anything the badges don't cover.

**Suggestions:** While the chat is empty, and again after every reply, the
badges suggest what you are likely to ask next. They depend on the tab you
are on and on the rig. In GUIDE they offer "How is guiding?" and, while
nothing is guiding, "Start guiding". In FOCUS they offer "Is my focus
good?" and "Run autofocus". While the dome or roof is open, "Close the
roof" comes first. After a reply, the badges also follow from what the
assistant just did: after it lists tonight's targets you get "Make a plan
with these", and after it reads the logs you get "How do I fix it?". On a
backend that can see images, LIVE and PREVIEW offer "Does this image look
good?"; on the text-only local tiers they offer "Take a test frame"
instead.

**Answers:** When the assistant needs you to choose (which target, which
filter, which of two fixes), it asks with the options as buttons, so one
tap answers it. The text field stays open while it waits, and if you type
an answer instead, the assistant takes the typed one. When a reply ends
with a yes-or-no question and doesn't carry its own options, the badges
start with **Yes** and **No**. (Buttons for a question with options work on
the cloud tier and with your own key; the local tiers still get the Yes and
No badges.)

**Your own questions:** Every question you send has a pin (📌) beside it.
When you tap the pin, the question becomes a badge of its own that comes
first in the row, so a question you repeat all night, "How is guiding?" for
example, is always one tap away. The pinned questions are kept in this
browser, up to eight of them. To remove one, tap its pin again, or open the
⚙ menu and tap it under **Pinned questions**.

On a touch screen the chat doesn't put the cursor in the text field after
each reply, because that would open the on-screen keyboard over the
badges.

## Canopus Assistant / Choosing a backend

You pick where the assistant runs in **Settings, Assistant**. There are
four choices, and the picker labels each by what runs it:

- **Cloud (subscription).** The most capable option, and the only one that
  can look at your frames. The AI runs on our servers (gpt-5.3-chat on
  Microsoft Azure); your browser relays approved actions to your local
  Polaris. It needs an internet connection and a subscription (US$4.99 per
  month at the time of writing, with a short free trial). Any device that
  can open the Polaris web interface can use it, no local model or GPU
  required.

- **On this host (free).** A small language model runs on the Polaris host
  itself, the machine Polaris is running on, so any phone or tablet on
  your network becomes a thin client. No account, no subscription, fully
  offline, text only. It needs a 64-bit host with roughly 6 GB of RAM free
  alongside Polaris; 8 GB or more and a faster processor let warm replies
  land in a few seconds (the first reply of a session takes longer while
  the model loads). Polaris downloads the model and runtime for you on
  first use.

- **On this device (free).** The assistant runs the model on the machine
  you are browsing from, using a local LLM server you already run (Ollama,
  LM Studio, or llama.cpp), or, inside the Polaris mobile app, a model
  bundled into the app. This is the fastest option on a powerful PC or Mac
  with a capable GPU, where replies are near-instant. No account, fully
  offline, text only. When Polaris and your local LLM are on the same
  machine, Polaris routes the calls for you, so there is nothing to
  configure; only a split setup, where you browse a Polaris host on a
  different machine, needs the LLM server to allow the Polaris page's
  origin.

- **Cloud API with your own key.** Polaris runs the agent on the host and
  calls a commercial AI provider with a key you supply: **Anthropic
  (Claude)**, **OpenAI (GPT)**, or any **OpenAI-compatible server**
  (OpenRouter, Groq, an Ollama on the LAN, anything that speaks the same
  protocol at a `/v1` root). You get a frontier model, the full tool
  catalogue, and frame analysis when the model you name can see images,
  with no Polaris subscription; you pay the provider directly for what you
  use, at their prices. This is the option for someone who already has an
  API account.

  Because the host makes the calls, the key is configured once and every
  phone, tablet and laptop on the network gets the same assistant. It also
  means the **host** needs internet access, which is the one practical
  difference from the subscription tier (see the "How it works" section).

The two local tiers are free and keep every message on your own hardware.
The subscription tier is the only one whose revenue funds Polaris; the
bring-your-own-key tier costs you whatever your provider charges and
nothing to us.

On a phone or tablet **browser**, "On this device" has no local model to
run, because the browser is not the machine hosting the model. Use "On
this host" instead (the model runs on the Polaris host and your phone is
just a screen), or install the Polaris mobile app, which can run a small
model natively on the phone.

## Canopus Assistant / Turning it on

Open **Settings, Assistant** and choose a backend.

For a **free local backend**, pick "On this host" or "On this device" and
follow the short setup shown in the card: download the on-host model, or
point Polaris at your local LLM and pick a model. The assistant's floating
button appears straight away; there is no account to create and nothing to
pay.

For **Cloud API with your own key**, the card grows a short form:

1. Pick the **Provider**. For an OpenAI-compatible server, fill in the
   **Base URL** as well: the `/v1` root, such as
   `https://openrouter.ai/api/v1` or `http://192.168.1.10:11434/v1`.
2. Name the **Model**. The field suggests current ids for the provider you
   picked, and takes any id your key can use. A vision-capable model is
   what lets Canopus look at your frames.
3. Paste the **API key** and press **Save**. The key is stored on the
   Polaris host, in a file only the Polaris user can read, and is never
   sent back to a browser: the card shows only whether a key is present,
   and offers **Clear key** to remove it.
4. **Test connection** asks the provider which models your key can see and
   reports whether the one you named is among them, which catches a typo
   or a wrong key before you are mid-session. **Start** brings the
   assistant up.

Switching backends restarts the assistant for every client of this host,
because the host runs one agent at a time.

For the **cloud tier**, the assistant also surfaces as a small, dismissible
badge in the bottom-right corner of the interface. The flow from there is
deliberately short:

1. Click the badge to open a short intro that explains what the assistant
   is and what it costs (and points you to the free local options, in case
   you would rather run it yourself).
2. Choose to subscribe. You create an account with just an email address,
   there is no password to invent. Polaris sends you a **magic link** (and
   a short numeric code you can type instead), and clicking it signs you
   in. The subscription itself is handled by Stripe's secure checkout.
3. Once you are subscribed, a floating action button replaces the badge.
   Click it to open the chat panel and start talking to the assistant.

If you dismiss the badge, it stays gone until there is something new to
show. Nothing about the cloud flow is baked into open-source Polaris: the
app only knows how to fetch a small manifest from the assistant's cloud
and render whatever it describes. With no manifest configured, the badge
never appears at all.

## Canopus Assistant / How it works

There are three architectures, depending on the backend, and all of them
share the same safety and privacy model.

**Cloud tier.** Three parts fit together:

- **The cloud service** holds the language model, the account and billing
  records, and the "agent loop" that turns your requests into a plan of
  tool calls. It is the only part that talks to the AI.
- **The chat panel** is a small web app served from that cloud and shown
  inside Polaris as an embedded frame.
- **Your browser is the bridge.** The cloud never contacts your telescope.
  When the agent decides to read the system status or slew the mount, it
  does not reach into your network; it hands that *intent* to your browser,
  which executes it against your local Polaris over your LAN, using the
  session you are already logged into, and hands the result back.

This has a happy consequence: because the browser is the bridge, your
Polaris host (the "Networking and Remote Access" chapter) never needs its own internet connection.
The single-board computer at the telescope can sit on an offline field
network; as long as the browser you are holding has both internet (to
reach the cloud) and LAN access (to reach Polaris), the assistant works.

**Local tiers.** There is no cloud and no server-side AI. The agent loop
runs in your browser (for "On this device") or on the Polaris host itself
(for "On this host"), talking to a local model over a plain HTTP endpoint.
Prompts and replies never leave your hardware. The same "browser is the
bridge" model still applies for actions: proposed tool calls run against
your local Polaris through the session you are logged into.

**Your own key.** The agent loop runs on the Polaris host, as it does for
"On this host", but instead of a small local model it calls your
provider's API over the internet. Tool calls therefore run on the host
directly, without the browser in the middle, which is simpler but inverts
the internet requirement: here the **host** needs to reach the provider,
and a rig on an offline field network cannot use this backend. Your key
never leaves the host, and no part of this involves our cloud service.

## Canopus Assistant / Staying in control

This applies to every backend. Read-only questions, "what's up tonight?",
"how's guiding?", "is this sub sharp?", run freely; they only look.
Anything that **moves hardware or changes a running session** is
different: slewing, autofocus, starting or stopping a sequence or live
stack, parking, a meridian flip. Those are never performed silently. The
assistant proposes an action, and it does nothing until you approve it.
You can review the exact steps, approve them, or reject them outright. The
approval gate is structural, not a matter of the model's good behaviour,
so it holds on the local tiers exactly as it does on the cloud. The
assistant is a co-pilot with its hands visible on the yoke, not an
autopilot you have to trust blindly.

## Canopus Assistant / Your account and subscription

This section applies to the subscription cloud tier only. The two local
backends have no account and nothing to pay, and the bring-your-own-key
backend has no account with us either: your account, your billing and your
usage limits are the provider's, and you manage them there.

The cloud tier is a flat monthly subscription (US$4.99 per month at the
time of writing; the current price is always shown on the subscribe
screen), with a short free trial for new subscribers. Sign-in is by magic
link, so there is no password to manage; you can change your email or
resend the link from the sign-in screen. Billing and cancellation go
through Stripe's standard customer flow.

To keep the shared AI service fair and its costs predictable, each
subscriber has a generous monthly usage allowance. In normal use you will
never notice it; if you ever reach it, the assistant tells you plainly and
picks up again at the start of the next cycle. Your current usage is shown
in the assistant's settings menu.

## Canopus Assistant / Privacy

On the **local tiers**, privacy is simple: nothing leaves your hardware.
There is no account, no cloud, and no third-party AI provider. Your
messages, the model, and every reply stay on the Polaris host or on the
device you are browsing from.

With **your own key**, nothing goes through us at all: the host talks
straight to the provider you chose, so your messages, and any frame the
assistant looks at, are handled under **that provider's** terms and
retention policy, not ours. Worth reading them once if the field matters
to you. The key sits on the host with owner-only permissions and is never
handed to a browser.

On the **cloud tier**, the assistant is designed to need very little about
you. It does not upload or store your images. When it "looks at a frame",
the analysis happens against your local Polaris, and only the *result*,
sharpness, star shape, a gradient note, travels back as text. Likewise,
when it needs to know how the session is going, your browser forwards a
compact, on-demand snapshot of status (not your data) so the agent can
reason about it. The account itself is just your email address plus the
subscription state. The full, current privacy policy is linked from the
intro screen and hosted alongside the service.

## Canopus Assistant / Why the cloud tier is a separate product

Polaris is licensed under the AGPLv3, which is what keeps it free and open.
The two local backends and the bring-your-own-key one are part of that open
codebase: the provider adapters ship with Polaris, so anyone can read
exactly what is sent to Anthropic or OpenAI on their behalf. The
subscription cloud tier is the one closed, commercial piece, and it is kept cleanly separable on
purpose: it runs as an entirely separate service and only ever talks to
Polaris through the same public web API that any other client uses.
Nothing in open-source Polaris contains the cloud tier's product logic; the
open app is just a generic, off-by-default host that renders what the cloud
manifest tells it to. That boundary is what lets a paid add-on fund a free
application without compromising either one.
