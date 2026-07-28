// Polaris Astro Controller — UI internationalization runtime.
//
// Model: "English source as key" (gettext-style). The English text that
// already lives in index.html / app.js IS the lookup key; a per-language
// catalog at /data/locales/{lang}.json maps
//   { "English source string": "Translated string" }.
// English is the identity language: no catalog, no fetch, no observer, ZERO
// overhead — the app behaves exactly as before for English users.
//
// For any other language a MutationObserver translates text nodes +
// title/placeholder/aria-label/alt attributes by looking up their current
// English content, so the ~2,500 existing literals (static AND Alpine x-text
// output) are translated WITHOUT editing thousands of bindings. Strings built
// in JS (toasts, chart labels, interpolated messages) are translated via the
// exposed t() / Alpine $t() helper with {var} interpolation.
//
// Language change reloads the page (rebuilds charts / WASM-derived UI for free
// and keeps the observer's "English -> target once per node" model simple).
//
// Loaded NON-deferred, BEFORE Alpine, so window.I18N + the $t magic are ready
// when Alpine hydrates. The FOUC gate (html.i18n-loading { body hidden }) is
// armed by the head IIFE in index.html for non-English and lifted here once the
// catalog is loaded and the first pass has run.
(function () {
    'use strict';

    var SUPPORTED = ['en', 'pt-BR', 'es', 'fr', 'de'];
    var LS_KEY = 'nina-ui-lang';

    function readLang() {
        try {
            var v = localStorage.getItem(LS_KEY);
            if (v && SUPPORTED.indexOf(v) >= 0) return v;
        } catch (_) { /* private mode */ }
        return 'en';
    }

    var lang = readLang();
    var catalog = Object.create(null);
    // Attributes whose values are user-visible and worth translating.
    var ATTRS = ['title', 'placeholder', 'aria-label', 'alt'];
    // Text that is purely numbers / units / symbols is never a catalog key —
    // skip it so the 1 Hz numeric status churn doesn't even hash-lookup.
    var NUMERIC = /^[\d\s.,:;%°"'+\-/x×()]+$/;

    // Collapse whitespace + trim so heavily-indented HTML text nodes
    // ("\n      Save\n    ") match a clean catalog key ("Save").
    function norm(s) { return s ? s.replace(/\s+/g, ' ').trim() : ''; }

    // Public translate-with-interpolation. For JS-built strings:
    //   t('Focus done in {s}s', { s: 5 })
    // Falls back to the (interpolated) source string when untranslated.
    function t(src, vars) {
        if (src == null) return src;
        var hit = catalog[norm(src)];
        var out = (hit != null && hit !== '') ? hit : String(src);
        if (vars) {
            out = out.replace(/\{(\w+)\}/g, function (m, k) {
                return Object.prototype.hasOwnProperty.call(vars, k) ? vars[k] : m;
            });
        }
        return out;
    }

    window.I18N = {
        get lang() { return lang; },
        supported: SUPPORTED,
        t: t,
        // Persist a new language. The caller decides when to reload (app.js
        // applyUiLang() reloads so the whole tree re-renders in the new lang).
        setLang: function (l) {
            if (SUPPORTED.indexOf(l) < 0) l = 'en';
            try { localStorage.setItem(LS_KEY, l); } catch (_) { }
            lang = l;
        }
    };

    // Register the Alpine $t magic for every language (identity for English),
    // so app.js can wrap built strings uniformly: $t('...') / this.$t('...').
    document.addEventListener('alpine:init', function () {
        try { window.Alpine.magic('t', function () { return t; }); } catch (_) { }
    });

    // English: nothing else to do. No fetch, no DOM walking, no observer.
    if (lang === 'en') return;

    // ---- Non-English: catalog load + DOM translation --------------------

    function lookup(raw) {
        var key = norm(raw);
        if (!key || NUMERIC.test(key)) return null;
        var hit = catalog[key];
        return (hit != null && hit !== '' && hit !== key) ? hit : null;
    }

    // A node is skipped if it (or an ancestor) opts out via data-no-i18n
    // (hot/numeric regions, terminal, charts, user data).
    //
    // data-i18n opts a subtree back IN. The nearest of the two attributes
    // wins, so an island of ordinary UI text can live inside an excluded
    // region: the activity bar is data-no-i18n as a whole, because its
    // readouts change several times a second and re-walking them is pure
    // waste, but the tray button in it is a normal labelled control whose
    // tooltip deserves translating like any other.
    //
    // An element carrying BOTH attributes counts as excluded: closest()
    // returns that element, and the safer reading of a contradiction is to
    // leave the text alone.
    function excluded(el) {
        if (!el || !el.closest) return false;
        var m = el.closest('[data-no-i18n], [data-i18n]');
        return !!m && m.hasAttribute('data-no-i18n');
    }
    function optedOut(node) {
        return excluded(node.nodeType === 3 ? node.parentElement : node);
    }

    function translateTextNode(node) {
        var raw = node.nodeValue;
        if (!raw) return;
        // Echo of our own write (Alpine didn't change it): skip.
        if (node.__i18nOut != null && node.nodeValue === node.__i18nOut) return;
        if (optedOut(node)) return;
        var tr = lookup(raw);
        if (tr == null) return;
        var lead = (raw.match(/^\s*/) || [''])[0];
        var trail = (raw.match(/\s*$/) || [''])[0];
        var next = lead + tr + trail;
        if (next !== node.nodeValue) {
            node.__i18nOut = next;
            node.nodeValue = next;
        }
    }

    function translateAttrs(el, only) {
        if (!el || el.nodeType !== 1 || !el.getAttribute) return;
        if (excluded(el)) return;
        var list = only ? [only] : ATTRS;
        for (var i = 0; i < list.length; i++) {
            var a = list[i];
            if (!el.hasAttribute(a)) continue;
            var raw = el.getAttribute(a);
            var tr = lookup(raw);
            if (tr != null && el.getAttribute(a) !== tr) el.setAttribute(a, tr);
        }
    }

    // One-time full pass over a root (the body on first paint, or an added
    // subtree). Walks text nodes + element attributes, pruning data-no-i18n.
    function translateTree(root) {
        if (!root) return;
        if (root.nodeType === 3) { translateTextNode(root); return; }
        if (root.nodeType !== 1) return;
        // Bail on an excluded root, unless it contains an opted-in island: the
        // per-node checks below would find it, but only if we get that far.
        if (excluded(root) && !(root.querySelector && root.querySelector('[data-i18n]'))) return;
        // Attributes on the root + descendants.
        translateAttrs(root);
        var els = root.querySelectorAll ? root.querySelectorAll('*') : [];
        for (var i = 0; i < els.length; i++) translateAttrs(els[i]);
        // Text nodes.
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        var n;
        var batch = [];
        while ((n = walker.nextNode())) batch.push(n);
        for (var j = 0; j < batch.length; j++) translateTextNode(batch[j]);
    }

    function startObserver() {
        var obs = new MutationObserver(function (muts) {
            for (var i = 0; i < muts.length; i++) {
                var m = muts[i];
                if (m.type === 'characterData') {
                    translateTextNode(m.target);
                } else if (m.type === 'attributes') {
                    translateAttrs(m.target, m.attributeName);
                } else if (m.type === 'childList') {
                    for (var k = 0; k < m.addedNodes.length; k++) {
                        translateTree(m.addedNodes[k]);
                    }
                }
            }
        });
        obs.observe(document.body, {
            subtree: true,
            childList: true,
            characterData: true,
            attributes: true,
            attributeFilter: ATTRS
        });
    }

    function reveal() {
        document.documentElement.classList.remove('i18n-loading');
    }

    // Load the catalog as early as possible (runs during parse).
    var loaded = fetch('/data/locales/' + encodeURIComponent(lang) + '.json', { cache: 'no-cache' })
        .then(function (r) { return r.ok ? r.json() : {}; })
        .then(function (j) { catalog = j || Object.create(null); })
        .catch(function () { catalog = Object.create(null); });

    // First full pass after Alpine has rendered x-text output, then observe.
    var did = false;
    function activate() {
        if (did) return; did = true;
        loaded.then(function () {
            try { translateTree(document.body); } catch (_) { }
            try { startObserver(); } catch (_) { }
            reveal();
        });
    }
    document.addEventListener('alpine:initialized', activate);
    // Safety: never leave the body hidden if Alpine fails to init.
    setTimeout(function () { activate(); reveal(); }, 4000);
})();
