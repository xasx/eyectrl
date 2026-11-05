namespace EyeCtrl.App.Browse;

/// <summary>
/// The bridge script injected into every document
/// (<c>CoreWebView2.AddScriptToExecuteOnDocumentCreated</c>): enumerates the page's interactive
/// elements and posts the full snapshot envelope the T1 parser consumes —
/// <c>{type:"elements", gen, dpr, els:[{i,k,r:[x,y,w,h]}]}</c> — where <c>r</c> rects are CSS-px
/// viewport rects and <c>dpr</c> is the live <c>window.devicePixelRatio</c>, so each snapshot is
/// self-describing (no DPI is ever baked in here; a Ctrl+wheel zoom simply changes dpr and the
/// rAF-throttled wheel/resize handlers repost). Content-free by contract (the KB-E1 standard):
/// ids, kinds and rects only — element text, labels, URLs and attributes never cross the bridge.
/// </summary>
internal static class BrowseElementScript
{
    /// <summary>The per-snapshot element cap: a runaway page cannot flood the dwell registry.</summary>
    public const int MaxElements = 400;

    /// <summary>The script source. Kept a C# const (no loose resource, no
    /// ExcludeFromSingleFile concern); the one side of the id/kind vocabulary that pairs with
    /// <c>WebElementMessageParser</c>'s other side. Ids carry the
    /// <c>WebElementInfo.IdPrefix</c> directly so the same string names the element in the
    /// detector and in the dwell-cue message posted back into the page.</summary>
    public const string Source = """
(function () {
    'use strict';

    var MAX = 400;
    var counter = 0;
    var gen = 0;
    var idMap = new WeakMap();      // element -> id, stable per document
    var ids = new Map();            // id -> element, so the dwell cue can find its target
    var outline = null;

    function visible(el) {
        var style = null;
        try { style = window.getComputedStyle(el); } catch (e) { return false; }
        if (!style || style.visibility === 'hidden' || style.display === 'none') return false;
        if (el.getAttribute('aria-hidden') === 'true' || el.hidden) return false;
        var rect = el.getBoundingClientRect();
        return rect.width > 0 && rect.height > 0;
    }

    function kindOf(el) {
        var tag = el.tagName;
        if (tag === 'A') return 'link';
        if (tag === 'SELECT') return 'select';
        if (tag === 'TEXTAREA') return 'textarea';
        if (tag === 'INPUT') {
            var type = (el.getAttribute('type') || 'text').toLowerCase();
            if (type === 'button' || type === 'submit' || type === 'reset' || type === 'image') return 'button';
            return 'input';
        }
        return 'button';
    }

    function accept(el) {
        if (el.id && ids.has(el.id)) return false;   // duplicated DOM id: first wins, like the parser
        var tag = el.tagName;
        var type = tag === 'INPUT' ? (el.getAttribute('type') || 'text').toLowerCase() : '';
        if (tag === 'A' && el.hasAttribute('href')) return true;
        if (tag === 'BUTTON' || tag === 'SELECT' || tag === 'TEXTAREA' || tag === 'SUMMARY') return true;
        if (tag === 'INPUT') {
            if (type === 'hidden' || type === 'password') return false;
            return true;
        }
        if (el.hasAttribute('onclick')) return true;
        var role = (el.getAttribute('role') || '').toLowerCase();
        return role === 'button' || role === 'link';
    }

    function idFor(el) {
        var id = idMap.get(el);
        if (!id) {
            counter += 1;
            id = 'web_e' + counter;
            idMap.set(el, id);
        }
        return id;
    }

    function collect(doc, depth, out) {
        var nodes = doc.querySelectorAll(
            'a[href], button, input, select, textarea, summary, [role=button], [role=link], [onclick]');
        for (var i = 0; i < nodes.length && out.length < MAX; i++) {
            var el = nodes[i];
            if (!visible(el) || !accept(el)) continue;
            var id = idFor(el);
            var rect = el.getBoundingClientRect();
            out.push({ i: id, k: kindOf(el), r: [rect.left, rect.top, rect.width, rect.height] });
            ids.set(id, el);
        }
        if (depth < 1) {
            var frames = doc.querySelectorAll('iframe');
            for (var f = 0; f < frames.length && out.length < MAX; f++) {
                var frame = frames[f];
                try {
                    var origin = new URL(frame.src, window.location.href).origin;
                    if (origin !== window.location.origin) continue;
                } catch (e) { continue; }
                try {
                    if (frame.contentDocument) collect(frame.contentDocument, depth + 1, out);
                } catch (e) { /* cross-origin: skipped */ }
            }
        }
    }

    function post() {
        var els = [];
        ids = new Map();
        gen += 1;
        try { collect(document, 0, els); } catch (e) { els = []; }
        try {
            window.chrome.webview.postMessage({
                type: 'elements',
                gen: gen,
                dpr: window.devicePixelRatio,
                els: els
            });
        } catch (e) { /* not hosted: drop */ }
    }

    // rAF throttle for the high-frequency reposts (scroll, resize, Ctrl+wheel zoom): one post
    // per frame at most, and a queued frame is not re-queued.
    var framePending = false;
    function postOnFrame() {
        if (framePending) return;
        framePending = true;
        requestAnimationFrame(function () {
            framePending = false;
            post();
        });
    }

    // Debounced structural repost: the MutationObserver can burst during a page's own
    // hydration; the snapshot is full, so only the last burst of a burst train matters.
    var debounce = null;
    function postDebounced() {
        if (debounce) clearTimeout(debounce);
        debounce = setTimeout(post, 150);
    }

    // The C#→JS cue: {type:'dwell', id, p} — draw (p > 0) or clear (p <= 0) the accent outline.
    function drawCue(id, p) {
        var el = p > 0 ? ids.get(id) : null;
        if (!el) { clearCue(); return; }
        var rect = el.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) { clearCue(); return; }
        if (!outline) {
            outline = document.createElement('div');
            outline.style.pointerEvents = 'none';
            outline.style.position = 'absolute';
            outline.style.zIndex = '2147483647';
            outline.style.border = '3px solid #FFB300';
            outline.style.boxSizing = 'border-box';
            (document.body || document.documentElement).appendChild(outline);
        }
        outline.style.left = (rect.left + window.scrollX) + 'px';
        outline.style.top = (rect.top + window.scrollY) + 'px';
        outline.style.width = rect.width + 'px';
        outline.style.height = rect.height + 'px';
        outline.style.display = 'block';
    }

    function clearCue() {
        if (outline) outline.style.display = 'none';
    }

    try {
        window.chrome.webview.addEventListener('message', function (event) {
            try {
                var data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
                if (data && data.type === 'dwell') drawCue(data.id, data.p);
            } catch (e) { /* malformed: ignored */ }
        });
    } catch (e) { /* not hosted: no cue */ }

    window.addEventListener('load', post);
    window.addEventListener('DOMContentLoaded', post);
    window.addEventListener('scroll', postOnFrame, { passive: true, capture: true });
    window.addEventListener('resize', postOnFrame);
    document.addEventListener('wheel', postOnFrame, { passive: true, capture: true });

    var observer = new MutationObserver(postDebounced);
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true });

    // AddScriptToExecuteOnDocumentCreated runs before the body exists; post once it is there.
    if (document.body) post();
})();
""";
}
