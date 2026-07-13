(function () {
    'use strict';
    // Touch tooltips. HTML `title` tooltips require hover, which touch devices
    // don't have, so on phones/tablets the "?" help icons and info glyphs are
    // dead. Surface them as a tap-to-reveal floating bubble.
    //
    // Scope is deliberately narrow: only the read-only help affordances
    //   .gain-help / .indi-help-icon / [role="img"][title] / [data-tip] / .tip-tap
    // get tap-to-reveal. We do NOT hijack long-press on arbitrary buttons —
    // the shutter (long-press = loop) and mount jog already own long-press, and
    // a global handler would fight those gestures. To make a normal titled
    // control tappable-for-tip, add class "tip-tap" to it.
    //
    // Coarse-pointer only, so desktop keeps its native hover tooltips untouched.
    // The bubble reads `title` live at tap time, so it picks up the i18n
    // MutationObserver's translation.
    var coarse = false;
    try { coarse = window.matchMedia('(pointer: coarse)').matches; } catch (e) { /* old browser */ }
    if (!coarse && !('ontouchstart' in window) && !(navigator.maxTouchPoints > 0)) return;

    var SEL = '.gain-help, .indi-help-icon, [data-tip], .tip-tap, [role="img"][title]';
    var bubble = null, hideTimer = null;

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
        // Measure off-screen, then place: below the element, flipped above if it
        // would overflow the viewport, and clamped horizontally.
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
        hideTimer = setTimeout(hide, 6000);
    }

    // Handle at the click phase (fires after a clean tap, never during a
    // scroll/drag) and capture so we win before Alpine handlers. Help icons do
    // nothing on click, so preventing default + propagation is harmless.
    document.addEventListener('click', function (e) {
        var el = e.target.closest && e.target.closest(SEL);
        if (el && tipText(el)) {
            e.preventDefault();
            e.stopPropagation();
            show(el);
            return;
        }
        if (bubble && bubble.classList.contains('show') && !bubble.contains(e.target)) hide();
    }, true);

    window.addEventListener('scroll', hide, true);
    window.addEventListener('resize', hide);
})();
