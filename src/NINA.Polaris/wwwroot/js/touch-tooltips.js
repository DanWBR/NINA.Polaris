(function () {
    'use strict';
    // Touch tooltips. HTML `title` tooltips require hover, which touch devices
    // don't have, so on phones/tablets titled controls are dead. Two reveal
    // gestures, both coarse-pointer only (desktop keeps its native hover):
    //
    //   1. TAP on a read-only help affordance (the "?" glyphs / info icons):
    //        .gain-help / .indi-help-icon / [role="img"][title] / [data-tip] /
    //        .tip-tap        -> they do nothing on tap, so a tap reveals the tip.
    //
    //   2. LONG-PRESS on a titled control inside a status bar (top + bottom) or
    //      any .tip-longpress container. Those chips/icons are CLICKABLE
    //      (navigate on tap), so we can't steal the tap — long-press reveals the
    //      tip and cancels the click that would otherwise follow. Scoped to the
    //      status bars on purpose: elements that own their own long-press
    //      gesture (the shutter = loop, the mount jog) are NOT in scope, so we
    //      never fight them.
    //
    // The bubble reads `title` live at reveal time, so it picks up the i18n
    // MutationObserver's translation.
    var coarse = false;
    try { coarse = window.matchMedia('(pointer: coarse)').matches; } catch (e) { /* old browser */ }
    if (!coarse && !('ontouchstart' in window) && !(navigator.maxTouchPoints > 0)) return;

    var TAP_SEL = '.gain-help, .indi-help-icon, [data-tip], .tip-tap, [role="img"][title]';
    var LP_SCOPE = '.status-bar, .stats-bar, .full-stats-panel, .phd2-statusbar, ' +
                   '.guide-bottombar, .files-statusbar, .tip-longpress';
    var LONG_PRESS = 450, MOVE_SLOP = 12, AUTO_HIDE = 6000;

    var bubble = null, hideTimer = null;
    var lpTimer = null, lpFired = false, lpX = 0, lpY = 0;

    function ensure() {
        if (bubble) return;
        bubble = document.createElement('div');
        bubble.className = 'touch-tip';
        bubble.setAttribute('role', 'tooltip');
        document.body.appendChild(bubble);
    }
    function tipText(el) {
        return (el.getAttribute('title') || el.getAttribute('data-tip') || '').trim();
    }
    function hide() { if (bubble) bubble.classList.remove('show'); }
    function show(el) {
        var t = tipText(el);
        if (!t) return;
        ensure();
        bubble.textContent = t;
        bubble.style.maxWidth = Math.min(340, window.innerWidth - 16) + 'px';
        // Measure off-screen, then place near the element, flipped above if it
        // would overflow, and clamped horizontally.
        bubble.style.left = '-9999px';
        bubble.style.top = '0px';
        bubble.classList.add('show');
        var r = el.getBoundingClientRect();
        var bb = bubble.getBoundingClientRect();
        var left = Math.max(8, Math.min(r.left + r.width / 2 - bb.width / 2,
                                        window.innerWidth - bb.width - 8));
        var top = r.bottom + 8;
        if (top + bb.height > window.innerHeight - 8) {
            top = Math.max(8, r.top - bb.height - 8);
        }
        bubble.style.left = left + 'px';
        bubble.style.top = top + 'px';
        clearTimeout(hideTimer);
        hideTimer = setTimeout(hide, AUTO_HIDE);
    }

    // Nearest titled ancestor that sits inside a long-press scope container.
    function titledInScope(node) {
        var t = node.closest && node.closest('[title], [data-tip]');
        if (!t || !tipText(t)) return null;
        return (t.closest && t.closest(LP_SCOPE)) ? t : null;
    }

    // ----- Long-press (status bars) -----
    document.addEventListener('pointerdown', function (e) {
        lpFired = false;
        clearTimeout(lpTimer); lpTimer = null;
        if (e.pointerType === 'mouse') return;
        var el = titledInScope(e.target);
        if (!el) return;
        lpX = e.clientX; lpY = e.clientY;
        lpTimer = setTimeout(function () { lpFired = true; show(el); }, LONG_PRESS);
    }, true);
    document.addEventListener('pointermove', function (e) {
        if (lpTimer && (Math.abs(e.clientX - lpX) > MOVE_SLOP ||
                        Math.abs(e.clientY - lpY) > MOVE_SLOP)) {
            clearTimeout(lpTimer); lpTimer = null;
        }
    }, true);
    function endLp() { clearTimeout(lpTimer); lpTimer = null; }
    document.addEventListener('pointerup', endLp, true);
    document.addEventListener('pointercancel', endLp, true);
    // Kill the native long-press context menu / selection inside scopes.
    document.addEventListener('contextmenu', function (e) {
        if (titledInScope(e.target)) e.preventDefault();
    }, true);

    // ----- Click phase: suppress the long-press click, else tap-reveal helps -----
    document.addEventListener('click', function (e) {
        if (lpFired) {                       // long-press just fired -> don't navigate
            e.preventDefault(); e.stopPropagation();
            lpFired = false;
            return;
        }
        var el = e.target.closest && e.target.closest(TAP_SEL);
        if (el && tipText(el)) {
            e.preventDefault(); e.stopPropagation();
            show(el);
            return;
        }
        if (bubble && bubble.classList.contains('show') && !bubble.contains(e.target)) hide();
    }, true);

    window.addEventListener('scroll', hide, true);
    window.addEventListener('resize', hide);
})();
