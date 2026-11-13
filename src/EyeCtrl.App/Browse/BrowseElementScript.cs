namespace EyeCtrl.App.Browse;

using System;
using EyeCtrl.Core.Browse;

/// <summary>
/// The bridge script injected into every document
/// (<c>CoreWebView2.AddScriptToExecuteOnDocumentCreated</c>): enumerates the page's interactive
/// elements and posts the full snapshot envelope the T1 parser consumes —
/// <c>{type:"elements", n, gen, dpr, els:[{i,k,r:[x,y,w,h]}]}</c> — where <c>r</c> rects are CSS-px
/// viewport rects (clipped to the viewport — review C3d: an element below the fold must not land
/// over the taskbar or the chrome row), <c>dpr</c> is the live <c>window.devicePixelRatio</c>, and
/// <c>n</c> is the per-summon bridge secret (<see cref="CreateSource"/>; the parser rejects any
/// envelope without it — review C3c). Content-free by contract (the KB-E1 standard): ids, kinds,
/// the secret and rects only — element text, labels, URLs and attributes never cross the bridge.
/// <para>
/// <c>AddScriptToExecuteOnDocumentCreated</c> runs in child frames too (review I2); the script
/// exits immediately there — frame elements are out of v1's scope, and frame posts would go to
/// the never-subscribed <c>CoreWebView2Frame.WebMessageReceived</c> anyway.
/// </para>
/// <para>
/// The script captures <c>chrome.webview.postMessage</c> ONCE, bound, at document creation
/// (review r2 M3) — a page hooking the bridge afterwards cannot route its posts through the
/// capture. This deters forgery by scripts that do not hook the bridge; a page that hooks the
/// bridge before posting can still read or append to the real envelope, which is why the
/// coordinator's clip and the parser's bounds are the load-bearing layers.
/// </para>
/// </summary>
internal static class BrowseElementScript
{
    /// <summary>The per-snapshot element cap the script enforces client-side; the parser
    /// enforces the same number inside its loop (<see cref="WebElementMessageParser.MaxElements"/>,
    /// pinned equal by the tests).</summary>
    public const int MaxElements = WebElementMessageParser.MaxElements;

    /// <summary>The per-summon secret placeholder inside <see cref="Template"/>.</summary>
    private const string SecretPlaceholder = "__BRIDGE_SECRET__";

    /// <summary>The script template. Kept a C# const (no loose resource, no
    /// ExcludeFromSingleFile concern); the one side of the id/kind vocabulary that pairs with
    /// <c>WebElementMessageParser</c>'s other side. Ids carry the
    /// <c>WebElementInfo.IdPrefix</c> directly so the same string names the element in the
    /// detector and in the dwell-cue message posted back into the page.</summary>
    private const string Template = """
(function () {
    'use strict';

    // Child frames exit (review I2): frame elements are out of v1's scope, and this script
    // runs in every document — a frame's own observers and posts would be pure waste.
    if (window.top !== window) return;

    var SECRET = '__BRIDGE_SECRET__';
    // Captured ONCE, bound, at document creation (review r2 M3): a page hooking
    // chrome.webview afterwards cannot route its posts through this capture. This deters
    // forgery by scripts that do not hook the bridge; the coordinator's clip and the parser's
    // bounds remain the load-bearing layers.
    var send = window.chrome.webview.postMessage.bind(window.chrome.webview);
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
        // Inert subtrees and disabled controls never act on a click, so dwelling one fires
        // nothing (review r2 M4): the guide's contract and the script now agree. `:disabled`
        // (r3) also covers an ancestor `<fieldset disabled>`, which `el.disabled` misses.
        try { if (el.closest('[inert]')) return false; } catch (e) { /* old engines: no closest */ }
        if (el.matches(':disabled') || el.getAttribute('aria-disabled') === 'true') return false;
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

    function collect(out) {
        var nodes = document.querySelectorAll(
            'a[href], button, input, select, textarea, summary, [role=button], [role=link], [onclick]');
        var vw = window.innerWidth;
        var vh = window.innerHeight;
        for (var i = 0; i < nodes.length && out.length < MAX; i++) {
            var el = nodes[i];
            if (!visible(el) || !accept(el)) continue;
            var rect = el.getBoundingClientRect();
            // Viewport clip (review C3d): an off-viewport element must not register — below the
            // fold it lands over the taskbar, above it over the chrome row. Partially visible
            // elements carry their clipped rect, so the click point stays on what is shown.
            if (rect.right <= 0 || rect.bottom <= 0 || rect.left >= vw || rect.top >= vh) continue;
            var left = Math.max(rect.left, 0);
            var top = Math.max(rect.top, 0);
            var right = Math.min(rect.right, vw);
            var bottom = Math.min(rect.bottom, vh);
            var id = idFor(el);
            out.push({ i: id, k: kindOf(el), r: [left, top, right - left, bottom - top] });
            ids.set(id, el);
        }
    }

    function post() {
        var els = [];
        ids = new Map();
        gen += 1;
        try { collect(els); } catch (e) { els = []; }
        try {
            send({
                type: 'elements',
                n: SECRET,
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

    /// <summary>
    /// The script source for one summon: the template with the bridge secret baked in
    /// (review C3c). The secret rides every envelope the script posts; the parser rejects
    /// any envelope without it. The secret deters forgery by scripts that do not hook the
    /// bridge: this script runs before the page's own scripts (so none can observe the
    /// capture at creation) and it never exposes the value on <c>window</c>, so a page
    /// cannot read it out of the DOM — but a script that hooks
    /// <c>chrome.webview.postMessage</c> can still read or forge envelopes, and the
    /// coordinator's clip and the parser's bounds stay the load-bearing layers.
    /// </summary>
    public static string CreateSource(string secret)
    {
        // A Guid "N" token (the only production caller) cannot carry these; a stray one in a
        // test must fail loudly here rather than silently break the envelope's JSON quoting.
        if (string.IsNullOrEmpty(secret) || secret.Contains('\'') || secret.Contains('\\'))
            throw new ArgumentException("The bridge secret must be non-empty and quote-free.", nameof(secret));

        return Template.Replace(SecretPlaceholder, secret, StringComparison.Ordinal);
    }
}
