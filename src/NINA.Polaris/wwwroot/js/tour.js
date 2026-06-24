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
                target: '[data-tour="live-actions"]', placement: 'left',
                title: 'LIVE — watch it stack',
                body: 'Frames stack in real time here (ASIAIR-style live view), so you watch the image build up as the session goes on.',
                before: function (a) { a.tab = 'live'; if (a.quickControlsCollapsed) a.quickControlsCollapsed = false; }
            },
            {
                target: '[data-tour="nav-help"]', placement: 'right',
                title: "That's the tour!",
                body: 'Full step-by-step tutorials and troubleshooting live in the Help tab. Clear skies!',
                before: function () { /* stay put */ }
            }
        ];
    }

    var state = { active: false, steps: null, idx: 0, els: null, raf: 0, onResize: null, onKey: null };

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

    function waitForTarget(step, cb) {
        if (!step.target) { requestAnimationFrame(function () { requestAnimationFrame(cb); }); return; }
        var tries = 0, max = 90; // ~1.5 s at 60 fps, then fall back to centered
        (function poll() {
            if (!state.active) return;
            var el = document.querySelector(step.target);
            var r = el && el.getBoundingClientRect();
            if (el && r && r.width > 0 && r.height > 0) {
                if (r.top < 4 || r.bottom > window.innerHeight - 4 || r.left < 4 || r.right > window.innerWidth - 4) {
                    try { el.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) { }
                    requestAnimationFrame(function () { requestAnimationFrame(cb); });
                    return;
                }
                cb(); return;
            }
            if (++tries > max) { cb(); return; }
            requestAnimationFrame(poll);
        })();
    }

    function go(i) {
        if (!state.active) return;
        i = clamp(i, 0, state.steps.length - 1);
        state.idx = i;
        var step = state.steps[i];
        try { if (step.before) step.before(app()); } catch (e) { }
        renderTipContent(step);
        waitForTarget(step, place);
    }

    function start(id) {
        var a = app();
        if (!a) return;
        if (a.auth && a.auth.needSetup) return;
        if (a.showLocationSetup) return;
        if (state.active) return;
        state.steps = introSteps(); // single "intro" tour for now
        state.idx = 0;
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
