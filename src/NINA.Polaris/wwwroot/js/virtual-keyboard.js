// Polaris on-screen keyboard.
//
// A self-contained, theme-aware virtual keyboard that docks to the right
// edge of the screen. Built for Android/iOS tablets where the native OS
// keyboard pops up over half the screen and fights with the full-screen
// Sky view / iframes. When the operator focuses a text or number input we
// suppress the native keyboard and show our own compact panel instead,
// styled with the Polaris CSS variables so it matches the active theme
// (including night mode). It dismisses on blur, on Enter/OK, or via the
// close button.
//
// No framework dependency: it self-initialises on DOMContentLoaded, binds
// through event delegation on `document`, and exposes a small global API
// (window.PolarisKeyboard) so the Settings UI can flip the mode.
//
// Opt-out per field: add `data-no-vkbd` (or wrap in an element carrying it).
// Force numeric/text per field: `data-vkbd="numeric"` / `data-vkbd="text"`.
(function () {
    'use strict';

    var MODE_KEY = 'polaris-vkbd-mode'; // 'auto' | 'on' | 'off'
    var POS_KEY = 'polaris-vkbd-pos';   // {left, top} px once the user drags it

    // User-chosen panel position (null = default bottom-right corner).
    var savedPos = (function () {
        try {
            var raw = localStorage.getItem(POS_KEY);
            if (!raw) return null;
            var p = JSON.parse(raw);
            if (p && typeof p.left === 'number' && typeof p.top === 'number') return p;
        } catch (e) { /* ignore */ }
        return null;
    })();
    function savePos(p) {
        savedPos = p;
        try { localStorage.setItem(POS_KEY, JSON.stringify(p)); } catch (e) { /* private mode */ }
    }

    // iOS ignores inputmode="none", so there we fall back to the readonly
    // trick (which blocks the native keyboard on every platform but loses
    // the native caret on number fields). Everywhere else inputmode="none"
    // keeps the field fully editable with a real caret + tap-to-position.
    var IS_IOS = (function () {
        var ua = navigator.userAgent || '';
        if (/iP(hone|od|ad)/.test(ua)) return true;
        // iPadOS 13+ reports as desktop Safari but has touch points.
        return /Mac/.test(navigator.platform || '') && navigator.maxTouchPoints > 1;
    })();

    function getMode() {
        try {
            var m = localStorage.getItem(MODE_KEY);
            return (m === 'on' || m === 'off' || m === 'auto') ? m : 'auto';
        } catch (e) { return 'auto'; }
    }
    function setMode(m) {
        if (m !== 'on' && m !== 'off' && m !== 'auto') m = 'auto';
        try { localStorage.setItem(MODE_KEY, m); } catch (e) { /* private mode */ }
        if (m === 'off') hide();
    }
    function isTouchDevice() {
        try { return window.matchMedia && window.matchMedia('(pointer: coarse)').matches; }
        catch (e) { return ('ontouchstart' in window) || navigator.maxTouchPoints > 0; }
    }
    // External components (e.g. the assistant panel) can temporarily suspend
    // the on-screen keyboard so it doesn't pop up in the same bottom-right
    // corner they occupy. Suspension overrides the mode and hides any panel
    // that is currently showing.
    var suspended = false;
    function setSuspended(v) {
        suspended = !!v;
        if (suspended) hide();
    }
    function isEnabled() {
        if (suspended) return false;
        var m = getMode();
        if (m === 'on') return true;
        if (m === 'off') return false;
        return isTouchDevice(); // auto
    }

    // ---- DOM (built lazily on first use) --------------------------------
    var root = null;       // the floating panel
    var keysHost = null;   // grid container that holds the current layout
    var titleEl = null;    // header label (shows the field's purpose)
    var current = null;    // currently bound input element
    var hideTimer = null;
    var shift = false;     // text layout caps state
    var layer = 'text';    // active text sub-layer: 'text' | 'symbols'

    function injectStyles() {
        if (document.getElementById('polaris-vkbd-style')) return;
        var css = [
            '.pvk-panel{position:fixed;right:10px;bottom:10px;z-index:99999;',
            '  width:320px;max-width:calc(100vw - 20px);',
            '  background:var(--bg-secondary,#16213e);color:var(--text-primary,#e0e0e0);',
            '  border:1px solid var(--border,#2a2a4a);border-radius:calc(var(--radius,6px) + 2px);',
            '  box-shadow:0 8px 32px rgba(0,0,0,.55);',
            '  font-family:var(--font-body,sans-serif);',
            '  user-select:none;-webkit-user-select:none;touch-action:manipulation;',
            '  display:none;flex-direction:column;overflow:hidden}',
            '.pvk-panel.pvk-numeric{width:248px}',
            '.pvk-panel.pvk-show{display:flex;animation:pvk-in .12s ease}',
            '@keyframes pvk-in{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:none}}',
            '.pvk-head{display:flex;align-items:center;gap:8px;padding:6px 8px;',
            '  cursor:move;touch-action:none;', // drag handle: reposition the panel
            '  background:var(--bg-tertiary,#0f3460);border-bottom:1px solid var(--border,#2a2a4a)}',
            '.pvk-title{flex:1;min-width:0;font-size:12px;color:var(--text-secondary,#a0a0b0);',
            '  white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
            '.pvk-x{flex:none;width:26px;height:26px;line-height:24px;text-align:center;',
            '  border:1px solid var(--border,#2a2a4a);border-radius:var(--radius,6px);',
            '  background:var(--bg-input,#0d1b30);color:var(--text-secondary,#a0a0b0);',
            '  cursor:pointer;font-size:14px}',
            '.pvk-keys{display:flex;flex-direction:column;gap:6px;padding:8px}',
            '.pvk-row{display:flex;gap:6px}',
            '.pvk-key{flex:1 1 0;min-width:0;height:42px;display:flex;align-items:center;',
            '  justify-content:center;font-size:16px;cursor:pointer;',
            '  background:var(--bg-input,#0d1b30);color:var(--text-primary,#e0e0e0);',
            '  border:1px solid var(--border,#2a2a4a);border-radius:var(--radius,6px);',
            '  transition:background .08s ease,transform .04s ease}',
            '.pvk-key.pvk-active{background:var(--accent,#2196f3);color:#fff;transform:translateY(1px)}',
            '.pvk-key.pvk-wide{flex-grow:1.6}',
            '.pvk-key.pvk-accent{background:var(--accent,#2196f3);color:#fff;border-color:var(--accent,#2196f3)}',
            '.pvk-key.pvk-mod{background:var(--bg-tertiary,#0f3460);color:var(--text-secondary,#a0a0b0);font-size:13px}',
            '.pvk-key.pvk-mod.pvk-on{background:var(--accent,#2196f3);color:#fff}',
            // night theme inherits via the CSS vars; nothing extra needed.
            ''
        ].join('\n');
        var st = document.createElement('style');
        st.id = 'polaris-vkbd-style';
        st.textContent = css;
        document.head.appendChild(st);
    }

    function buildPanel() {
        injectStyles();
        root = document.createElement('div');
        root.className = 'pvk-panel';
        root.setAttribute('role', 'dialog');
        root.setAttribute('aria-label', 'On-screen keyboard');

        var head = document.createElement('div');
        head.className = 'pvk-head';
        titleEl = document.createElement('div');
        titleEl.className = 'pvk-title';
        var x = document.createElement('div');
        x.className = 'pvk-x';
        x.textContent = '✕';
        x.setAttribute('data-action', 'close');
        head.appendChild(titleEl);
        head.appendChild(x);

        keysHost = document.createElement('div');
        keysHost.className = 'pvk-keys';

        root.appendChild(head);
        root.appendChild(keysHost);
        document.body.appendChild(root);

        // Keep focus on the input: suppress the default focus-stealing of a
        // pointerdown on the panel, and perform the key action right there
        // (so a single tap both keeps focus and types).
        root.addEventListener('pointerdown', onPointerDown, true);
        // Belt-and-suspenders for browsers that still emit mousedown.
        root.addEventListener('mousedown', function (e) { e.preventDefault(); }, true);

        // Drag the panel by its header to reposition it. Starts from the head
        // only (not the ✕ close button), keeps input focus (preventDefault),
        // and persists the position so it stays put across sessions.
        head.addEventListener('pointerdown', startDrag);
        // Keep the panel on-screen if the viewport shrinks / rotates.
        window.addEventListener('resize', function () { if (savedPos) applyPos(savedPos.left, savedPos.top); });
    }

    // Clamp + apply an absolute left/top, switching off the default
    // right/bottom corner anchoring.
    function applyPos(left, top) {
        if (!root) return;
        var w = root.offsetWidth || 320, h = root.offsetHeight || 300;
        left = Math.min(Math.max(0, left), Math.max(0, window.innerWidth - w));
        top = Math.min(Math.max(0, top), Math.max(0, window.innerHeight - h));
        root.style.left = left + 'px';
        root.style.top = top + 'px';
        root.style.right = 'auto';
        root.style.bottom = 'auto';
    }

    function startDrag(e) {
        // Let the ✕ (and any future header buttons) work normally.
        if (e.target.closest && e.target.closest('[data-action]')) return;
        e.preventDefault();
        var rect = root.getBoundingClientRect();
        var offX = e.clientX - rect.left, offY = e.clientY - rect.top;
        function move(ev) { applyPos(ev.clientX - offX, ev.clientY - offY); }
        function up() {
            document.removeEventListener('pointermove', move, true);
            document.removeEventListener('pointerup', up, true);
            var r = root.getBoundingClientRect();
            savePos({ left: r.left, top: r.top });
        }
        document.addEventListener('pointermove', move, true);
        document.addEventListener('pointerup', up, true);
    }

    // ---- Layout definitions ---------------------------------------------
    // Each entry is a row of keys; a key is a string (char) or an object
    // {label, action?, char?, cls?}.
    var NUMERIC = [
        ['7', '8', '9', { label: '⌫', action: 'backspace', cls: 'pvk-mod' }],
        ['4', '5', '6', { label: 'C', action: 'clear', cls: 'pvk-mod' }],
        ['1', '2', '3', '.'],
        ['-', '0', { label: 'OK', action: 'enter', cls: 'pvk-accent pvk-wide' }]
    ];

    var TEXT_ROWS = [
        ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'],
        ['q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p'],
        ['a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l'],
        [{ label: '⇧', action: 'shift', cls: 'pvk-mod' }, 'z', 'x', 'c', 'v', 'b', 'n', 'm',
            { label: '⌫', action: 'backspace', cls: 'pvk-mod' }]
    ];
    var SYMBOL_ROWS = [
        ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0'],
        ['-', '_', '/', ':', ';', '(', ')', '$', '&', '@'],
        ['.', ',', '?', '!', '\'', '"', '+', '*', '#'],
        [{ label: 'ABC', action: 'layer-text', cls: 'pvk-mod' }, '=', '%', '<', '>', '[', ']',
            { label: '⌫', action: 'backspace', cls: 'pvk-mod' }]
    ];
    function textBottomRow() {
        return [
            { label: layer === 'text' ? '?123' : 'ABC', action: layer === 'text' ? 'layer-symbols' : 'layer-text', cls: 'pvk-mod' },
            ',',
            { label: 'space', action: 'space', cls: 'pvk-wide' },
            '.',
            { label: 'OK', action: 'enter', cls: 'pvk-accent' }
        ];
    }

    function makeKey(spec) {
        var el = document.createElement('div');
        el.className = 'pvk-key';
        var label, action, char;
        if (typeof spec === 'string') {
            char = spec; label = spec; action = 'char';
        } else {
            action = spec.action || 'char';
            char = spec.char != null ? spec.char : (spec.label || '');
            label = spec.label != null ? spec.label : char;
            if (spec.cls) el.className += ' ' + spec.cls;
        }
        // Letters reflect shift state.
        if (action === 'char' && /^[a-z]$/.test(char) && shift) {
            char = char.toUpperCase(); label = char;
        }
        el.textContent = label;
        el.setAttribute('data-action', action);
        if (action === 'char') el.setAttribute('data-char', char);
        if (action === 'shift' && shift) el.classList.add('pvk-on');
        return el;
    }

    function renderRow(specs) {
        var row = document.createElement('div');
        row.className = 'pvk-row';
        specs.forEach(function (s) { row.appendChild(makeKey(s)); });
        return row;
    }

    function renderLayout(kind) {
        keysHost.innerHTML = '';
        if (kind === 'numeric') {
            root.classList.add('pvk-numeric');
            NUMERIC.forEach(function (r) { keysHost.appendChild(renderRow(r)); });
        } else {
            root.classList.remove('pvk-numeric');
            var rows = layer === 'symbols' ? SYMBOL_ROWS : TEXT_ROWS;
            rows.forEach(function (r) { keysHost.appendChild(renderRow(r)); });
            keysHost.appendChild(renderRow(textBottomRow()));
        }
    }

    // ---- Editing the bound field ----------------------------------------
    function canSelect(el) {
        try { return el.selectionStart !== null && el.selectionStart !== undefined; }
        catch (e) { return false; } // number/email inputs throw on access
    }

    function applyEdit(transform) {
        var el = current;
        if (!el) return;
        var v = el.value != null ? String(el.value) : '';
        if (canSelect(el)) {
            var s = el.selectionStart, e = el.selectionEnd;
            var res = transform(v, s, e);
            el.value = res.value;
            try { el.setSelectionRange(res.caret, res.caret); } catch (err) { /* noop */ }
        } else {
            // No selection API (e.g. type=number): edit at the end.
            var res2 = transform(v, v.length, v.length);
            el.value = res2.value;
        }
        el.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function insertChar(ch) {
        applyEdit(function (v, s, e) {
            return { value: v.slice(0, s) + ch + v.slice(e), caret: s + ch.length };
        });
        // Auto-release a one-shot shift after a letter (caps-lock feel kept
        // off: tap shift again to lock-style retype). We keep it sticky so
        // typing acronyms is easy; users tap shift again to drop it.
    }
    function backspace() {
        applyEdit(function (v, s, e) {
            if (s !== e) return { value: v.slice(0, s) + v.slice(e), caret: s };
            if (s > 0) return { value: v.slice(0, s - 1) + v.slice(s), caret: s - 1 };
            return { value: v, caret: s };
        });
    }
    function clearAll() {
        applyEdit(function () { return { value: '', caret: 0 }; });
    }

    function confirmField() {
        var el = current;
        if (!el) return;
        el.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
        el.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        hide();
        try { el.blur(); } catch (e) { /* noop */ }
    }

    function onPointerDown(e) {
        var key = e.target.closest ? e.target.closest('[data-action]') : null;
        if (!key || !root.contains(key)) return;
        e.preventDefault(); // keep focus + caret on the input
        var action = key.getAttribute('data-action');
        key.classList.add('pvk-active');
        setTimeout(function () { key.classList.remove('pvk-active'); }, 110);

        switch (action) {
            case 'char': insertChar(key.getAttribute('data-char')); break;
            case 'space': insertChar(' '); break;
            case 'backspace': backspace(); break;
            case 'clear': clearAll(); break;
            case 'shift': shift = !shift; renderLayout('text'); break;
            case 'layer-symbols': layer = 'symbols'; renderLayout('text'); break;
            case 'layer-text': layer = 'text'; renderLayout('text'); break;
            case 'enter': confirmField(); break;
            case 'close': hide(); try { if (current) current.blur(); } catch (er) { } break;
        }
    }

    // ---- Native-keyboard suppression ------------------------------------
    function suppressNative(el) {
        // <input type="number"> sanitises .value: assigning intermediate
        // strings like "5." or "-" silently blanks the field, and the
        // selection API is unavailable. Swap it to type="text" while we
        // edit so caret/selection work and partial values are accepted;
        // we normalise + restore the type on hide.
        if (el.tagName === 'INPUT' && (el.getAttribute('type') || '').toLowerCase() === 'number') {
            el.setAttribute('data-pvk-type', 'number');
            el.type = 'text';
        }
        if (IS_IOS) {
            // Idempotent: preArmIOS may have already flipped readOnly on the
            // pointerdown that preceded this focus, so only snapshot the
            // ORIGINAL value once (otherwise we'd save our own "true").
            if (!el.hasAttribute('data-pvk-ro'))
                el.setAttribute('data-pvk-ro', el.readOnly ? '1' : '0');
            el.readOnly = true; // blocks iOS keyboard; we still set value via JS
        } else {
            el.setAttribute('data-pvk-im', el.getAttribute('inputmode') || '');
            el.setAttribute('inputmode', 'none'); // hide keyboard, keep caret
        }
    }
    function restoreNative(el) {
        if (!el) return;
        if (el.hasAttribute('data-pvk-ro')) {
            el.readOnly = el.getAttribute('data-pvk-ro') === '1';
            el.removeAttribute('data-pvk-ro');
        }
        if (el.hasAttribute('data-pvk-im')) {
            var im = el.getAttribute('data-pvk-im');
            if (im) el.setAttribute('inputmode', im); else el.removeAttribute('inputmode');
            el.removeAttribute('data-pvk-im');
        }
        if (el.hasAttribute('data-pvk-type')) {
            // Normalise a half-typed number ("5." → "5", lone "-"/"." → "")
            // before handing the field back to its native number type, which
            // would otherwise blank an invalid string and lose the digits.
            var v = (el.value || '').trim();
            if (v.endsWith('.')) v = v.slice(0, -1);
            if (v === '-' || v === '.' || v === '-.') v = '';
            if (v !== el.value) {
                el.value = v;
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }
            el.type = el.getAttribute('data-pvk-type');
            el.removeAttribute('data-pvk-type');
        }
    }

    // ---- Eligibility -----------------------------------------------------
    var SKIP_TYPES = {
        checkbox: 1, radio: 1, range: 1, color: 1, file: 1, button: 1,
        submit: 1, reset: 1, image: 1, date: 1, time: 1, 'datetime-local': 1,
        month: 1, week: 1, hidden: 1
    };
    function eligible(el) {
        if (!el || !el.tagName) return false;
        var tag = el.tagName;
        if (tag === 'TEXTAREA') return !el.closest('[data-no-vkbd]');
        if (tag !== 'INPUT') return false;
        var type = (el.getAttribute('type') || 'text').toLowerCase();
        if (SKIP_TYPES[type]) return false;
        if (el.disabled || el.closest('[data-no-vkbd]')) return false;
        return true;
    }
    function layoutFor(el) {
        // If we already swapped this number field to text (see suppressNative),
        // keep treating it as numeric.
        if (el.getAttribute('data-pvk-type') === 'number') return 'numeric';
        var forced = (el.getAttribute('data-vkbd') || '').toLowerCase();
        if (forced === 'numeric') return 'numeric';
        if (forced === 'text') return 'text';
        var type = (el.getAttribute('type') || 'text').toLowerCase();
        if (type === 'number' || type === 'tel') return 'numeric';
        var im = (el.getAttribute('inputmode') || '').toLowerCase();
        if (im === 'numeric' || im === 'decimal' || im === 'tel') return 'numeric';
        if (el.classList.contains('vk-numeric')) return 'numeric';
        return 'text';
    }
    function fieldTitle(el) {
        // Best-effort human label for the header.
        if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
        if (el.placeholder) return el.placeholder;
        if (el.id) {
            var lbl = document.querySelector('label[for="' + CSS.escape(el.id) + '"]');
            if (lbl && lbl.textContent.trim()) return lbl.textContent.trim();
        }
        if (el.name) return el.name;
        return layoutFor(el) === 'numeric' ? 'Number' : 'Text';
    }

    // ---- Show / hide -----------------------------------------------------
    function show(el) {
        if (!root) buildPanel();
        if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
        current = el;
        shift = false;
        layer = 'text';
        // Resolve the layout + title *before* suppressNative, which may swap
        // a number input to type="text" (and thus change what layoutFor sees).
        var kind = layoutFor(el);
        var title = fieldTitle(el);
        suppressNative(el);
        // Swapping input.type can drop focus on some engines; reclaim it and
        // drop the caret at the end so the first key appends naturally.
        if (document.activeElement !== el) { try { el.focus(); } catch (e) { } }
        if (canSelect(el)) {
            try { var n = (el.value || '').length; el.setSelectionRange(n, n); } catch (e) { }
        }
        renderLayout(kind);
        titleEl.textContent = title;
        root.classList.add('pvk-show');
        // Restore the user's dragged position now the panel has real dimensions
        // (clamping needs offsetWidth/Height, which are 0 while display:none).
        if (savedPos) applyPos(savedPos.left, savedPos.top);
    }
    function hide() {
        if (root) root.classList.remove('pvk-show');
        if (current) { restoreNative(current); current = null; }
    }

    function onFocusIn(e) {
        if (!isEnabled()) return;
        var el = e.target;
        if (!eligible(el)) return;
        show(el);
    }
    function onFocusOut(e) {
        if (e.target !== current) return;
        // Delay so a tap that moves focus to the panel (or another field)
        // doesn't flicker the keyboard closed. The panel's pointerdown is
        // preventDefault-ed, so focus normally never leaves on a key tap;
        // this guards the edge cases.
        hideTimer = setTimeout(function () {
            if (document.activeElement !== current) hide();
        }, 120);
    }

    // iOS opens the native keyboard the instant an editable field gains focus
    // — that happens on the tap BEFORE our focusin handler runs, so setting
    // readOnly in show() is too late and the native keyboard pops up (and
    // covers our on-screen panel). Pre-arm readOnly on the pointerdown/
    // touchstart that precedes the focus so iOS never decides to show it. Only
    // on iOS + when the on-screen keyboard is enabled; restoreNative() undoes
    // it on blur. The snapshot in suppressNative is guarded to stay idempotent.
    function preArmIOS(e) {
        if (!IS_IOS || !isEnabled()) return;
        var t = e.target;
        var el = (t && t.closest) ? t.closest('input, textarea') : null;
        if (!el || !eligible(el)) return;
        if (!el.hasAttribute('data-pvk-ro')) {
            el.setAttribute('data-pvk-ro', el.readOnly ? '1' : '0');
            el.readOnly = true;
        }
    }
    document.addEventListener('pointerdown', preArmIOS, true);
    document.addEventListener('touchstart', preArmIOS, true);

    document.addEventListener('focusin', onFocusIn, true);
    document.addEventListener('focusout', onFocusOut, true);
    // Hide if the document loses focus entirely (e.g. switching apps).
    window.addEventListener('blur', function () { /* keep open; tablets blur often */ });

    window.PolarisKeyboard = {
        getMode: getMode,
        setMode: setMode,
        isEnabled: isEnabled,
        setSuspended: setSuspended,
        hide: hide
    };
})();
