# The Gaze Browser's JS Bridge Contract (v1)

**Status:** Shipped with the gaze-browser plan (T1–T5); trust model + frame scope from the
external review round (C1–C3, I1, I2).
**Code:** injected script `src/EyeCtrl.App/Browse/BrowseElementScript.cs` (a C# template — no
loose resource, no `ExcludeFromSingleFile` concern), parser
`src/EyeCtrl.Core/Browse/WebElementMessageParser.cs`, reconciler
`src/EyeCtrl.Core/Browse/WebElementSetReconciler.cs`, dwell surface
`src/EyeCtrl.ViewModels/BrowseViewModel.cs`, host `src/EyeCtrl.App/Browse/WebView2BrowseHost.cs`.

This is the contract between the bridge script injected into every document
(`CoreWebView2.AddScriptToExecuteOnDocumentCreated`) and the app that consumes its messages.
Both sides live in this repo; the parser's tests (`WebElementMessageParserTests`) are the
executable half of this document.

## Envelope: page → app

The script posts, via `window.chrome.webview.postMessage`, one JSON object per snapshot:

```json
{
  "type": "elements",
  "n": "0f4c9a1e…",
  "gen": 12,
  "dpr": 2,
  "els": [
    { "i": "web_e3", "k": "link", "r": [120, 340, 180, 32] }
  ]
}
```

| Field | Meaning | Rejection rule |
|---|---|---|
| `type` | Must be `"elements"` | anything else (or missing) fails the whole parse |
| `n` | The per-summon bridge secret (see the trust model) | missing, non-string, or ≠ the host's secret fails the whole parse |
| `gen` | Per-document snapshot counter, integral, ≥ 0 (`3` and `3.0` both accepted) | fractional, negative, or non-numeric fails the whole parse |
| `dpr` | The live `window.devicePixelRatio` | ≤ 0, non-numeric, or non-finite fails the whole parse |
| `els` | The element array (may be empty) | missing, or not an array, fails the whole parse |
| `i` | Element id, ≤ 256 chars, non-empty | broken element **dropped**, siblings survive |
| `k` | Coarse kind: `link` \| `button` \| `input` \| `select` \| `textarea` \| `other` (case-insensitive) | missing or unrecognised → `other` (still actionable) |
| `r` | `[x, y, w, h]` — the element's **CSS-pixel viewport rect**, clipped to the viewport | wrong arity, non-numeric or non-finite entry → dropped |

An `els` entry that is not an object is dropped. A rect that survives parsing is clamped, in
physical pixels, to the same ±100 000 px band as the external-dwell bridge
(`ExternalDwellService.ToRect`): positions to `[-100k, 100k]`, sizes to `[0, 100k]` — the clamp
bounds magnitudes so layout math downstream stays finite. It does **not** bound locations to the
browser window; that is the coordinator's clip (trust model below). An overflow pair (huge CSS ×
huge dpr) is dropped rather than carried as infinity.

## Coordinates and the dpr rule

The script reports rects in **CSS pixels relative to the viewport** and the **runtime
`window.devicePixelRatio`** in the same envelope. The C# side converts:
`physical = css × dpr + origin`, where `origin` is the WebView2 client area's top-left corner in
physical screen pixels, read from the window (`PointToScreen`) at snapshot time
(`IBrowseWindowSurface.TryGetWebOrigin`). **No DPI value is ever baked in** — each snapshot is
self-describing, and the 200 % display simply arrives as `dpr: 2`. A Ctrl+wheel zoom changes
`devicePixelRatio`; the rAF-throttled wheel/resize handlers repost the snapshot and the new dpr
rides along.

## Trust model (what a page can and cannot do)

Every page the user visits runs script in the same web view, and `window.chrome.webview.postMessage`
is available to all of it — so the bridge is treated as a hostile channel. The layers, in the
order a forged envelope meets them (external review C3):

1. **Sender check (host).** `WebMessageReceived` fires for the top-level document only (frame
   posts go to the never-subscribed `CoreWebView2Frame.WebMessageReceived`). The host drops any
   message whose `e.Source` is not the current top-level document URI — a document that has
   navigated away cannot speak for the new page.
2. **Bridge secret (parser).** The host generates a random per-summon secret; the injected
   script bakes it into every envelope (`n`) and the parser rejects any envelope without it.
   The script runs before the page's own scripts and never exposes the value on `window`, so a
   page cannot read it out of the DOM. This kills *forged* envelopes — but a page can still
   honestly report its own DOM however it likes, which is why the next layer is the decisive one.
3. **Viewport clip (script) and window clip (coordinator).** The script clips every rect to the
   viewport (an element below the fold would otherwise land over the taskbar; one scrolled just
   above would land over the chrome row). The coordinator re-checks app-side: every element rect
   is intersected with the web view's on-screen bounds (`TryGetWebBounds`, the two-corner
   `PointToScreen` pattern) and dropped when it does not intersect; the click point is the
   **clipped** rect's center. A page cannot register a dwell target outside the browser, however
   it lies about its own DOM.
4. **Host-side cap (coordinator).** Envelopes beyond 400 elements are dropped wholesale
   (`AddTarget` rebuilds the detector snapshot per add — tens of thousands of elements would
   otherwise stall the UI thread, and a stalled UI thread is all input gone for a gaze-only
   user).

**Residual, accepted:** a page can still place a target anywhere *inside* the browser's web view
by lying about its own DOM — the worst case is a mis-click onto the page the user is looking at,
never outside it. Chrome targets (Back/Close/…) live outside the web view and cannot be covered
by page rects. The `gen` counter is informational (ordering is not guaranteed by the SDK); the
per-document staleness is handled by the sender check instead.

## Selector set

The script collects, per document — **top-level documents only** (external review I2: frame
elements' rects would be frame-local and click off-target; the script exits immediately in child
frames, where its posts would go to the never-subscribed frame event anyway):

```
a[href], button, input, select, textarea, summary, [role=button], [role=link], [onclick]
```

- `input[type=hidden]` and `input[type=password]` are **excluded** — the keyboard's
  password-privacy rule applies to the browse bridge too: no dwell target may sit on a field
  whose text is secret.
- Elements are visibility-filtered: `visibility: hidden`, `display: none`, `aria-hidden`,
  `[hidden]` and zero-area rects are skipped, and off-viewport elements are skipped (the
  viewport clip above).
- Kind mapping: `a[href]` → link; `select` → select; `textarea` → textarea; `input`
  (button/submit/reset/image types → button, everything else → input); everything else
  (button, summary, role/onclick hits) → button.

## Ids and stability

Ids come from a per-document `WeakMap` counter: `web_e<N>` — already carrying the parser's
`WebElementInfo.IdPrefix`, so the **same string** names the element in the dwell registry and in
the dwell-cue message posted back into the page. Ids are stable for the element's lifetime in
its document (the WeakMap holds them across snapshots); a navigation starts a new document and a
new id space (`ClearWebTargets` clears the old targets — on `NavigationStarting`, before the new
document's script runs, so a static page's first snapshot survives; external review I1). A page
with duplicated DOM `id` attributes still yields one bridge id per *element* (the WeakMap keys
on the element, not the DOM id — external review M4 removed a check that wrongly skipped an
element whose DOM id looked like a generated one).

## Cadence and caps

A full snapshot is posted:

- on `DOMContentLoaded` and on `load` (the script itself runs before the body exists);
- debounced 150 ms behind `MutationObserver` bursts (childList/subtree/attributes);
- rAF-throttled on scroll, resize and wheel (one post per frame at most — this is what makes a
  scroll or a zoom repost instead of streaming).

The script caps every snapshot at **400 elements** (`BrowseElementScript.MaxElements`) and the
coordinator enforces the same cap on everything that arrives (trust model layer 4). The
reconciler's move rule (T1: displacement of either axis beyond 24 physical px re-registers the
element fresh and discards its accumulated dwell; smaller drift rebases in place and preserves
it) lives in Core, not in the script.

## App → page: the dwell cue

The app posts `{"type":"dwell","id":...,"p":0..1}` (via `PostWebMessageAsJson`) as the dwell
progress changes: `p > 0` draws a pointer-events-none accent outline over the element's current
rect (the script re-reads the rect each draw, so it tracks scrolling); `p ≤ 0` — or an unknown
id — clears it. The payload is serialized with System.Text.Json because the id originates from
the page. While the interaction state is Paused the app sends no page cue at all (external
review M10: a suppressed activation must not advertise itself in the page).

## Privacy: the bridge is content-free

Ids, coarse kinds, the secret and rects only. **No element text, no labels, no URLs, no
attribute values ever cross the bridge in either direction** — a text plus the layout would
spell the page's content, and the KB-E1 keyboard standard (content-free log lines) applies here
unchanged. Unknown fields anywhere in the envelope are ignored, never logged.

## Single-file publish note

The repo's shipped artifact is a `PublishSingleFile` self-contained exe (AGENTS.md, build &
run):

```
dotnet publish src/EyeCtrl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Under that publish mode the WebView2 native loader (`WebView2Loader.dll`) **is** loose beside
the exe (measured by the external review round: present at the output root and under
`runtimes/win-x64/native/`, next to `onnxruntime.dll`; `Microsoft.Web.WebView2.Core.dll`
P/Invokes it by name). `tools/release/Publish-Signed.ps1` lists it under
`$RequiredNativeDlls` (external review M12), so a package that lost it fails the release script
instead of shipping a browser that cannot start. **No launch of the published exe has happened
yet** — publishing and launching are different acts, and launching the real overlay app is
deliberately gated behind `EYECTRL_UI_TESTS=1` (AGENTS.md: it seizes the OS cursor from a
gaze-only user). When filing issues, distinguish the two failure classes
`WebView2BrowseHost.Describe` separates:

| Symptom | Cause | User-facing notice |
|---|---|---|
| `Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException` during environment creation | the **WebView2 runtime is not installed** on the machine | "This PC is missing the browser component (WebView2). …" |
| `DllNotFoundException` / `FileNotFoundException` while constructing the host | the **native loader failed to load** under single-file (corrupt extraction, blocked temp dir, stripped bundle) | "A browser component failed to load. …" |

Either way the coordinator logs the reason class, shows the notice in the browser's address bar
and **keeps the window open** — the chrome Close dwell is the exit, so a missing runtime
degrades without stranding the user and never crashes startup (the host is constructed only on
first summon; external review M1/M2: the notice stays open, is plain language, and never embeds
the raw exception message). Reproduction tip: a machine without the runtime fails at environment
creation before any loader question; a loader failure reproduces on a machine that HAS the
runtime, only under the single-file publish.

## Deferred in v1 (recorded verbatim from the plan, plus the review round's additions)

- tabs
- history / bookmarks / downloads / favorites
- frames of any origin (same-origin one-hop support was removed with review I2 — frame-local
  rects clicked off-target) and shadow DOM
- `window.open`/`target=_blank` popups, `script` dialogs, download and permission prompts
  (review I5: new-window requests are redirected onto the same tab; the other prompts are
  mouse-only dead ends until a later phase)
- elements EyeCtrl cannot see as clickable: disabled controls, `inert`/`pointer-events:none`,
  `opacity:0` elements, and JS-listener-only clickables without a role (review M5)
- native select-dropdown option navigation
- a dedicated dwell-time settings knob (targets carry `ToolbarButtonDwellMs`)
- external-UIA fallback for non-WebView2 apps
- settings-UI rows
