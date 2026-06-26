// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. See <https://www.gnu.org/licenses/>.

// Interactive introductory tour (coach-marks + auto panel switching).
//
// Self-contained module (same IIFE pattern as virtual-keyboard.js). It is
// PURELY additive: it never touches equipment or fires API calls — it only
// reads/sets the Alpine UI navigation state (window.__alpineRoot.tab and the
// sub-tab fields) and draws a spotlight + arrow + tooltip over real controls.
// So it cannot disturb Slew & Center, plate solving, captures, or any in-flight
// job. Exposes window.PolarisTour { start, stop, maybeOfferFirstRun }.
(function () {
    'use strict';

    var SEEN_KEY = 'polaris-intro-tour-seen';

    function app() { return window.__alpineRoot || null; }

    // ---------------------------------------------------------------------
    // Intro tour step list. Each step optionally switches the app to the tab
    // (and sub-tab) where its anchor lives, via `before(a)`, then the engine
    // waits for the element to become visible and spotlights it. A step with
    // no `target` renders a centered welcome/finish card.
    // ---------------------------------------------------------------------
    function introSteps() {
        return [
            {
                center: true,
                title: 'Welcome to N.I.N.A. Polaris',
                body: 'This quick tour walks through the main screens and the order you use them in on a typical night. You can leave any time with Skip, or replay it later from the Help tab.',
                before: function (a) { a.tab = 'home'; }
            },
            {
                target: '[data-tour="nav-rail"]', placement: 'right',
                title: 'The sidebar',
                body: 'Every screen lives here. The order roughly follows a session: equipment, sky, focus, guide, capture, then review your frames.',
                before: function (a) { a.tab = 'home'; }
            },
            {
                target: '[data-tour="rigs-connect"]', placement: 'bottom',
                title: 'RIGS — your equipment',
                body: 'Pick a rig, connect to INDI or Alpaca, then use "Connect all" to bring every selected device online at once.',
                before: function (a) { a.tab = 'equip'; if (a.equipTab !== undefined) a.equipTab = 'equipment'; if (a._applyTabSideEffects) a._applyTabSideEffects('equip'); }
            },
            {
                target: '[data-tour="sky-search"]', placement: 'bottom',
                title: 'SKY — find a target',
                body: 'Search for an object by name or click the map, frame it, then send the mount there. You can also center on the Sun, Moon or a planet.',
                before: function (a) { a.tab = 'sky'; if (a._applyTabSideEffects) a._applyTabSideEffects('sky'); }
            },
            {
                target: '[data-tour="preview-actions"]', placement: 'bottom',
                title: 'PREVIEW — test frames & plate solve',
                body: 'Snap single frames to check framing and focus, and plate-solve to confirm exactly where the scope is pointing.',
                before: function (a) { a.tab = 'preview'; }
            },
            {
                target: '[data-tour="focus-actions"]', placement: 'left',
                title: 'FOCUS — sharpen the stars',
                body: 'Run an automatic V-curve focus or focus by hand watching the HFR. Good focus is the single biggest quality win.',
                before: function (a) { a.tab = 'focus'; if (a.focusTab !== undefined) a.focusTab = 'vcurve'; }
            },
            {
                target: '[data-tour="guide-actions"]', placement: 'bottom',
                title: 'GUIDE — track accurately',
                body: 'Calibrate and start guiding (built-in or PHD2) so long exposures stay pin-sharp all night.',
                before: function (a) { a.tab = 'guide'; }
            },
            {
                target: '[data-tour="autorun-actions"]', placement: 'bottom',
                title: 'AUTORUN — shoot the night',
                body: 'Build a capture schedule — filters, exposures, counts, dithering, meridian flip — and let it run unattended.',
                before: function (a) { a.tab = 'sequence'; if (a.autorunTab !== undefined) a.autorunTab = 'sequence'; if (a._applyTabSideEffects) a._applyTabSideEffects('sequence'); }
            },
            {
                target: '[data-tour="nav-live"]', placement: 'right',
                title: 'LIVE — watch it stack',
                body: 'Open the LIVE view: frames stack in real time here (ASIAIR-style), so you watch the image build up as the session goes on.',
                before: function (a) { a.tab = 'live'; if (a.quickControlsCollapsed) a.quickControlsCollapsed = false; }
            },
            {
                target: '[data-tour="nav-studio"]', placement: 'right',
                title: 'STUDIO — stack & process',
                body: "After the night, STUDIO is your processing workspace. Let's walk through its tools, from raw frames to a finished image.",
                before: function (a) { a.tab = 'files'; if (a.filesInit) a.filesInit(); }
            },
            {
                target: '[data-tour="studio-stack"]', placement: 'left',
                title: 'Stacking',
                body: 'Drop your lights (and calibration frames) into the Stack workspace and integrate them into a single master — building masters, calibrating, and combining channels into RGB/LRGB.',
                before: function (a) { a.tab = 'files'; }
            },
            {
                target: '[data-tour="studio-starremoval"]', placement: 'bottom',
                title: 'Star removal',
                body: 'Split the master into a starless image + a stars-only image (StarNet, in the browser) — the basis for star reduction and processing the nebula separately.',
                before: function (a) { a.tab = 'files'; }
            },
            {
                target: '[data-tour="studio-bge"]', placement: 'bottom',
                title: 'Background extraction (BGE)',
                body: 'Remove light-pollution gradients and uneven background with GraXpert, for a flat, neutral sky.',
                before: function (a) { a.tab = 'files'; }
            },
            {
                target: '[data-tour="studio-denoise"]', placement: 'bottom',
                title: 'Denoise',
                body: 'Knock down noise with the GraXpert AI denoiser while preserving faint detail.',
                before: function (a) { a.tab = 'files'; }
            },
            {
                target: '[data-tour="studio-decon"]', placement: 'bottom',
                title: 'Deconvolution',
                body: 'Sharpen stars and structure with GraXpert deconvolution, recovering detail softened by seeing and optics.',
                before: function (a) { a.tab = 'files'; }
            },
            {
                target: '[data-tour="studio-editor"]', placement: 'bottom',
                title: 'Image editor',
                body: 'Open the Lightroom-style editor for the final touches: stretch, curves, colour calibration, crop, and export.',
                before: function (a) { a.tab = 'files'; }
            },
            {
                target: '[data-tour="nav-help"]', placement: 'right',
                title: "That's the tour!",
                body: 'Full step-by-step tutorials live in the Help tab — including two optional deep-dives: a tour of the top/bottom status bars and a walk through every Settings card. Clear skies!',
                before: function () { /* stay put */ }
            }
        ];
    }

    // Optional deep-dive: the top header bar + the bottom activity bar. Each
    // item uses skipIfMissing so chips that only appear in certain states
    // (battery, camera temp, transfers…) are skipped rather than shown empty.
    function statusbarSteps() {
        return [
            {
                center: true,
                title: 'Status bars',
                body: "Polaris frames every screen between two status bars. Let's go over what each item means — top bar first, then the bottom one.",
                before: function (a) { a.tab = 'home'; }
            },
            {
                target: '.brand', placement: 'bottom', skipIfMissing: true,
                title: 'Top bar — app + version',
                body: 'The N.I.N.A. Polaris logo (click it to jump Home) and the running version number.'
            },
            {
                target: '[data-tour="statusbar-indi"]', placement: 'bottom', skipIfMissing: true,
                title: 'INDI status',
                body: 'Whether the INDI server is connected. Green = connected, grey = off. Click it to jump to the Equipment tab.'
            },
            {
                target: '[data-tour="statusbar-alpaca"]', placement: 'bottom', skipIfMissing: true,
                title: 'Alpaca status',
                body: 'ASCOM Alpaca devices discovered on the network, with a count. Click to open Equipment on the Alpaca source.'
            },
            {
                target: '[data-tour="statusbar-phd2"]', placement: 'bottom', skipIfMissing: true,
                title: 'Guiding status',
                body: "The guider's connection + state (idle, guiding, lost…). Click to open the Guide tab."
            },
            {
                target: '.status-clock', placement: 'bottom', skipIfMissing: true,
                title: 'Chips + clock',
                body: 'This area shows live chips when relevant — current-exposure progress, camera temperature, stacked-frame count, this device\'s battery — plus the wall clock.'
            },
            {
                target: '.log-badge', placement: 'bottom', skipIfMissing: true,
                title: 'Debug log',
                body: 'Opens the in-app log. The badge turns amber/red with a count when there are unread warnings or errors.'
            },
            {
                target: '[data-tour="statusbar-night"]', placement: 'bottom', skipIfMissing: true,
                title: 'Night mode',
                body: 'Toggle the red, dark-adapted colour scheme for use at the telescope.'
            },
            {
                target: '.fullscreen-badge', placement: 'bottom', skipIfMissing: true,
                title: 'Fullscreen',
                body: 'Hide the browser chrome — handy on a mini-PC kiosk or a tablet at the scope.'
            },
            {
                target: '.ui-lock-badge', placement: 'bottom', skipIfMissing: true,
                title: 'Lock UI',
                body: 'Block accidental taps: the screen stays visible but only the floating unlock pill is clickable.'
            },
            {
                target: '[data-tour="statusbar-stats"]', placement: 'top', skipIfMissing: true,
                title: 'Bottom — capture stats',
                body: 'In LIVE/PREVIEW the bottom stats line shows the latest frame quality: detected stars, HFR (focus), mean level, SNR, frame count and stacking state.',
                before: function (a) { a.tab = 'live'; }
            },
            {
                target: '.activity-bar-ops', placement: 'top', skipIfMissing: true,
                title: 'Activity chips',
                body: 'The footer shows what the server is busy with right now — running jobs, background tasks, warnings — as compact chips.'
            },
            {
                target: '.activity-net', placement: 'top', skipIfMissing: true,
                title: 'Network traffic',
                body: 'Live client↔server data rate: ↓ received and ↑ sent, so you can see frames and previews flowing.'
            },
            {
                target: '.activity-bar-host', placement: 'top', skipIfMissing: true,
                title: 'Host stats',
                body: 'The server machine at a glance: CPU, RAM, free disk, device model, and a clock-skew warning if the server clock drifts. Disk colour warns before you run out of space.'
            },
            {
                center: true,
                title: 'Status bars — done',
                body: 'Those two bars give you situational awareness from any screen. Back to imaging!'
            }
        ];
    }

    // Optional deep-dive: walk every SETTINGS card. Cards that don't exist on
    // this platform (e.g. WiFi/Power/HTTPS on non-SBC hosts) are skipped.
    function settingsSteps() {
        var open = function (a) { a.tab = 'settings'; };
        return [
            { center: true, title: 'Settings', body: "Let's walk through the Settings cards one by one — what each one is for. Cards that don't apply to your device are skipped.", before: open },
            { target: '[data-tour="set-https"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'HTTPS certificate', body: 'Generate/install a TLS certificate so the browser trusts the server over HTTPS — needed for WebGPU and secure remote access.' },
            { target: '[data-tour="set-storage"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Auto-push to network storage', body: 'Automatically copy saved frames to a NAS / network share as they are written.' },
            { target: '[data-tour="set-appearance"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Appearance', body: 'UI language, theme, font and density.' },
            { target: '[data-tour="set-skyimg"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Sky imagery (offline DSS)', body: 'Download deep-sky survey tiles for offline use, so the Sky map shows real imagery without internet.' },
            { target: '[data-tour="set-terminal"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Remote terminal', body: 'An in-browser SSH terminal to the Polaris host (or any Linux box on your LAN). Credentials are per-session and never saved.' },
            { target: '[data-tour="set-auth"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Authentication', body: 'Set or change the password that protects remote access to Polaris.' },
            { target: '[data-tour="set-devicename"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Device name', body: 'The friendly name shown in the browser tab, mDNS (nina.local) and on the network.' },
            { target: '[data-tour="set-platesolve"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Plate solving', body: 'Pick and configure the solver (ASTAP, Astrometry.net…) used by Slew & Center and recovery.' },
            { target: '[data-tour="set-clock"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Clock', body: "Sync the server clock — important because accurate time drives the mount's coordinate calculations." },
            { target: '[data-tour="set-power"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Power', body: 'Shut down or reboot the server host (SBC) safely from the UI.' },
            { target: '[data-tour="set-network"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Network (WiFi)', body: 'View and switch the WiFi network the server is connected to.' },
            { target: '[data-tour="set-observatory"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Observatory', body: 'Your site latitude/longitude/altitude — used for the sky, twilight, altitude charts and GoTo.' },
            { target: '[data-tour="set-imageoutput"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Image output', body: 'Shows where captured frames are saved (the Studio root); the folder picker itself lives in the FILES tab.' },
            { target: '[data-tour="set-imagecache"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Image cache', body: 'Polaris keeps rendered previews + thumbnails on disk; here you see usage and can clear it.' },
            { target: '[data-tour="set-gpu"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'GPU acceleration (OpenCL)', body: 'Use the GPU to speed up image math (stacking/stretch) when an OpenCL device is available.' },
            { target: '[data-tour="set-benchmark"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Hardware benchmark', body: 'Measure this host\'s processing speed and compare it against reference boards.' },
            { target: '[data-tour="set-hardware"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Hardware', body: 'Auto-connect your gear when the server starts: it connects INDI, runs an Alpaca discovery, then connects every device saved on the active rig. Off by default.' },
            { target: '[data-tour="set-debuglog"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Debug logging', body: 'Control log verbosity and optionally persist the debug log to disk for troubleshooting.' },
            { target: '[data-tour="set-tools"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'External tools', body: 'Paths to optional external programs (e.g. Siril, GraXpert) Polaris can hand off to.' },
            { target: '[data-tour="set-ai"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'AI inference (ONNX)', body: 'Manage the AI models (star removal, denoise, deconvolution, BGE) and where they run (browser/NPU/CLI).' },
            { target: '[data-tour="set-reset"]', placement: 'bottom', skipIfMissing: true, before: open, title: 'Reset to factory defaults', body: 'Danger zone — wipe all settings and start fresh. Use only as a last resort.' },
            { center: true, title: 'Settings — done', body: "That's the full Settings tour. You can revisit it any time from Help." }
        ];
    }

    var state = { active: false, steps: null, idx: 0, dir: 1, els: null, raf: 0, onResize: null, onKey: null };

    // Tour registry — start(id) picks the step list. 'intro' is the main
    // overview; 'statusbars' and 'settings' are the optional deep-dives.
    function tourSteps(id) {
        if (id === 'statusbars') return statusbarSteps();
        if (id === 'settings') return settingsSteps();
        return introSteps();
    }

    function clamp(v, lo, hi) { return Math.max(lo, Math.min(v, hi)); }

    function ensureEls() {
        if (state.els) return state.els;
        var overlay = document.createElement('div');
        overlay.className = 'tour-overlay';
        var hole = document.createElement('div'); hole.className = 'tour-hole';
        var arrow = document.createElement('div'); arrow.className = 'tour-arrow';
        var tip = document.createElement('div'); tip.className = 'tour-tip';
        tip.innerHTML =
            '<div class="tour-tip-title"></div>' +
            '<div class="tour-tip-body"></div>' +
            '<div class="tour-tip-foot">' +
            '<span class="tour-tip-count"></span>' +
            '<span class="tour-tip-btns">' +
            '<button class="tour-btn tour-skip" type="button">Skip</button>' +
            '<button class="tour-btn tour-back" type="button">Back</button>' +
            '<button class="tour-btn tour-next tour-btn-primary" type="button">Next</button>' +
            '</span></div>';
        overlay.appendChild(hole);
        overlay.appendChild(arrow);
        overlay.appendChild(tip);
        document.body.appendChild(overlay);
        tip.querySelector('.tour-skip').addEventListener('click', function () { stop(); });
        tip.querySelector('.tour-back').addEventListener('click', function () { go(state.idx - 1); });
        tip.querySelector('.tour-next').addEventListener('click', function () {
            if (state.idx >= state.steps.length - 1) stop(); else go(state.idx + 1);
        });
        state.els = { overlay: overlay, hole: hole, arrow: arrow, tip: tip };
        return state.els;
    }

    function renderTipContent(step) {
        var els = state.els;
        els.tip.querySelector('.tour-tip-title').textContent = step.title || '';
        els.tip.querySelector('.tour-tip-body').textContent = step.body || '';
        els.tip.querySelector('.tour-tip-count').textContent = (state.idx + 1) + ' / ' + state.steps.length;
        var back = els.tip.querySelector('.tour-back');
        var next = els.tip.querySelector('.tour-next');
        back.style.visibility = state.idx === 0 ? 'hidden' : 'visible';
        next.textContent = state.idx >= state.steps.length - 1 ? 'Finish' : 'Next';
    }

    function centerTip() {
        var els = state.els, tip = els.tip;
        tip.style.maxWidth = Math.min(420, window.innerWidth - 24) + 'px';
        var tw = tip.offsetWidth, th = tip.offsetHeight;
        tip.style.left = clamp((window.innerWidth - tw) / 2, 12, window.innerWidth - tw - 12) + 'px';
        tip.style.top = clamp((window.innerHeight - th) / 2, 12, window.innerHeight - th - 12) + 'px';
    }

    function positionTip(r, placement) {
        var els = state.els, tip = els.tip, arrow = els.arrow;
        tip.style.maxWidth = Math.min(360, window.innerWidth - 24) + 'px';
        var tw = tip.offsetWidth, th = tip.offsetHeight, gap = 14;
        var place = placement || 'auto';
        if (place === 'auto') {
            place = (window.innerHeight - r.bottom) >= th + gap ? 'bottom'
                : r.top >= th + gap ? 'top'
                : (window.innerWidth - r.right) >= tw + gap ? 'right'
                : r.left >= tw + gap ? 'left' : 'bottom';
        }
        var cx = r.left + r.width / 2, cy = r.top + r.height / 2, top, left;
        if (place === 'bottom') { top = r.bottom + gap; left = cx - tw / 2; }
        else if (place === 'top') { top = r.top - gap - th; left = cx - tw / 2; }
        else if (place === 'right') { left = r.right + gap; top = cy - th / 2; }
        else { left = r.left - gap - tw; top = cy - th / 2; }
        left = clamp(left, 12, window.innerWidth - tw - 12);
        top = clamp(top, 12, window.innerHeight - th - 12);
        tip.style.left = left + 'px';
        tip.style.top = top + 'px';

        arrow.className = 'tour-arrow';
        var ax, ay, cls;
        if (place === 'bottom') { cls = 'tour-arrow-up'; ax = clamp(cx, left + 18, left + tw - 18); ay = top; }
        else if (place === 'top') { cls = 'tour-arrow-down'; ax = clamp(cx, left + 18, left + tw - 18); ay = top + th; }
        else if (place === 'right') { cls = 'tour-arrow-left'; ax = left; ay = clamp(cy, top + 18, top + th - 18); }
        else { cls = 'tour-arrow-right'; ax = left + tw; ay = clamp(cy, top + 18, top + th - 18); }
        arrow.classList.add(cls);
        arrow.style.left = ax + 'px';
        arrow.style.top = ay + 'px';
    }

    function place() {
        if (!state.active || !state.els) return;
        var step = state.steps[state.idx], pad = 6;
        var target = step.target ? document.querySelector(step.target) : null;
        var r = target ? target.getBoundingClientRect() : null;
        var visible = r && r.width > 0 && r.height > 0;
        if (target && visible) {
            var h = state.els.hole;
            h.style.display = 'block';
            h.style.top = (r.top - pad) + 'px';
            h.style.left = (r.left - pad) + 'px';
            h.style.width = (r.width + pad * 2) + 'px';
            h.style.height = (r.height + pad * 2) + 'px';
            positionTip(r, step.placement);
        } else {
            // No anchor (welcome/finish) or not found → centered card, no spotlight.
            state.els.hole.style.display = 'none';
            state.els.arrow.className = 'tour-arrow';
            centerTip();
        }
    }

    // Resolve a step's target: wait (briefly) for it to exist + be visible,
    // scrolling it into view. Calls cb(found) — found=false means the anchor
    // never showed up (e.g. a platform-specific Settings card that isn't
    // rendered on this host).
    function waitForTarget(step, cb) {
        if (!step.target) { requestAnimationFrame(function () { requestAnimationFrame(function () { cb(true); }); }); return; }
        var tries = 0, max = 60; // ~1 s at 60 fps, then give up
        (function poll() {
            if (!state.active) return;
            var el = document.querySelector(step.target);
            var r = el && el.getBoundingClientRect();
            if (el && r && r.width > 0 && r.height > 0) {
                if (r.top < 4 || r.bottom > window.innerHeight - 4 || r.left < 4 || r.right > window.innerWidth - 4) {
                    try { el.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) { }
                    requestAnimationFrame(function () { requestAnimationFrame(function () { cb(true); }); });
                    return;
                }
                cb(true); return;
            }
            if (++tries > max) { cb(false); return; }
            requestAnimationFrame(poll);
        })();
    }

    function go(i) {
        if (!state.active) return;
        i = clamp(i, 0, state.steps.length - 1);
        state.dir = i >= state.idx ? 1 : -1;
        state.idx = i;
        var step = state.steps[i];
        try { if (step.before) step.before(app()); } catch (e) { }
        renderTipContent(step);
        waitForTarget(step, function (found) {
            // Auto-skip a missing optional anchor (status-bar / Settings tours)
            // by continuing in the current direction, so absent platform cards
            // don't leave a stranded centered card. The closing step has no
            // target, so forward-skipping always terminates cleanly.
            if (!found && step.skipIfMissing) {
                var ni = state.idx + state.dir;
                if (ni >= 0 && ni < state.steps.length) { go(ni); return; }
            }
            place();
        });
    }

    function start(id) {
        var a = app();
        if (!a) return;
        if (a.auth && a.auth.needSetup) return;
        if (a.showLocationSetup) return;
        if (state.active) return;
        state.steps = tourSteps(id);
        state.idx = 0;
        state.dir = 1;
        state.active = true;
        ensureEls();
        state.els.overlay.classList.add('active');
        document.body.classList.add('tour-open');
        state.onResize = function () {
            if (state.raf) return;
            state.raf = requestAnimationFrame(function () { state.raf = 0; place(); });
        };
        window.addEventListener('resize', state.onResize);
        window.addEventListener('scroll', state.onResize, true);
        state.onKey = function (e) {
            if (e.key === 'Escape') { e.preventDefault(); stop(); }
            else if (e.key === 'ArrowRight' || e.key === 'Enter') { e.preventDefault(); if (state.idx >= state.steps.length - 1) stop(); else go(state.idx + 1); }
            else if (e.key === 'ArrowLeft') { e.preventDefault(); go(state.idx - 1); }
        };
        window.addEventListener('keydown', state.onKey, true);
        go(0);
    }

    function stop() {
        if (!state.active) return;
        state.active = false;
        try { localStorage.setItem(SEEN_KEY, '1'); } catch (e) { }
        if (state.els) {
            state.els.overlay.classList.remove('active');
            state.els.hole.style.display = 'none';
        }
        document.body.classList.remove('tour-open');
        if (state.onResize) {
            window.removeEventListener('resize', state.onResize);
            window.removeEventListener('scroll', state.onResize, true);
        }
        if (state.onKey) window.removeEventListener('keydown', state.onKey, true);
        if (state.raf) { cancelAnimationFrame(state.raf); state.raf = 0; }
    }

    // One-time first-run offer card (bottom-right). Either choice marks the
    // tour "seen" so it never nags again; the Help tab can always replay it.
    function maybeOfferFirstRun() {
        try { if (localStorage.getItem(SEEN_KEY)) return; } catch (e) { }
        var a = app();
        if (!a || (a.auth && a.auth.needSetup) || a.showLocationSetup) return;
        if (document.querySelector('.tour-offer')) return;
        var card = document.createElement('div');
        card.className = 'tour-offer';
        card.innerHTML =
            '<div class="tour-offer-title">New to Polaris?</div>' +
            '<div class="tour-offer-body">Take a quick interactive tour of the main screens.</div>' +
            '<div class="tour-offer-btns">' +
            '<button class="tour-btn tour-offer-skip" type="button">Not now</button>' +
            '<button class="tour-btn tour-btn-primary tour-offer-start" type="button">Start tour</button>' +
            '</div>';
        document.body.appendChild(card);
        function dismiss() { try { localStorage.setItem(SEEN_KEY, '1'); } catch (e) { } card.remove(); }
        card.querySelector('.tour-offer-skip').addEventListener('click', dismiss);
        card.querySelector('.tour-offer-start').addEventListener('click', function () { card.remove(); start('intro'); });
    }

    window.PolarisTour = { start: start, stop: stop, maybeOfferFirstRun: maybeOfferFirstRun };

    // Offer the tour after Alpine boots — but wait until the password / location
    // setup modals (if any) are dismissed so we never fight them.
    document.addEventListener('alpine:initialized', function () {
        var tries = 0;
        setTimeout(function tick() {
            tries++;
            var a = app();
            if (a && !(a.auth && a.auth.needSetup) && !a.showLocationSetup) { maybeOfferFirstRun(); return; }
            if (tries < 30) setTimeout(tick, 1500);
        }, 2000);
    });
})();
