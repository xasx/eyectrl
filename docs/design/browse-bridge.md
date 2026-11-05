# The Gaze Browser's JS Bridge Contract (v1)

**Status:** Shipped with the gaze-browser plan (T1–T5).
**Code:** injected script `src/EyeCtrl.App/Browse/BrowseElementScript.cs` (a C# const — no loose
resource, no `ExcludeFromSingleFile` concern), parser
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
| `gen` | Per-document snapshot counter, integral, ≥ 0 (`3` and `3.0` both accepted) | fractional, negative, or non-numeric fails the whole parse |
| `dpr` | The live `window.devicePixelRatio` | ≤ 0, non-numeric, or non-finite fails the whole parse |
| `els` | The element array (may be empty) | missing, or not an array, fails the whole parse |
| `i` | Element id, ≤ 256 chars, non-empty | broken element **dropped**, siblings survive |
| `k` | Coarse kind: `link` \| `button` \| `input` \| `select` \| `textarea` \| `other` (case-insensitive) | missing or unrecognised → `other` (still actionable) |
| `r` | `[x, y, w, h]` — the element's **CSS-pixel viewport rect** | wrong arity, non-numeric or non-finite entry → dropped |

An `els` entry that is not an object is dropped. A rect that survives parsing is clamped, in
physical pixels, to the same ±100 000 px band as the external-dwell bridge
(`ExternalDwellService.ToRect`): positions to `[-100k, 100k]`, sizes to `[0, 100k]` — a hostile
or buggy page can never push dwell registration or click mapping out of bounds. An overflow
pair (huge CSS × huge dpr) is dropped rather than carried as infinity.

## Coordinates and the dpr rule

The script reports rects in **CSS pixels relative to the viewport** and the **runtime
`window.devicePixelRatio`** in the same envelope. The C# side converts:
`physical = css × dpr + origin`, where `origin` is the WebView2 client area's top-left corner in
physical screen pixels, read from the window (`PointToScreen`) at snapshot time
(`IBrowseWindowSurface.TryGetWebOrigin`). **No DPI value is ever baked in** — each snapshot is
self-describing, and the 200 % display simply arrives as `dpr: 2`. A Ctrl+wheel zoom changes
`devicePixelRatio`; the rAF-throttled wheel/resize handlers repost the snapshot and the new dpr
rides along.

## Selector set

The script collects, per document:

```
a[href], button, input, select, textarea, summary, [role=button], [role=link], [onclick]
```

- `input[type=hidden]` and `input[type=password]` are **excluded** — the keyboard's
  password-privacy rule applies to the browse bridge too: no dwell target may sit on a field
  whose text is secret.
- Elements are visibility-filtered: `visibility: hidden`, `display: none`, `aria-hidden`,
  `[hidden]` and zero-area rects are skipped.
- Same-origin iframes are recursed **one hop**. Cross-origin frames and shadow DOM beyond that
  are out of reach (see the deferred list).
- Kind mapping: `a[href]` → link; `select` → select; `textarea` → textarea; `input`
  (button/submit/reset/image types → button, everything else → input); everything else
  (button, summary, role/onclick hits) → button.

## Ids and stability

Ids come from a per-document `WeakMap` counter: `web_e<N>` — already carrying the parser's
`WebElementInfo.IdPrefix`, so the **same string** names the element in the dwell registry and in
the dwell-cue message posted back into the page. Ids are stable for the element's lifetime in
its document (the WeakMap holds them across snapshots); a navigation starts a new document and a
new id space (`ClearWebTargets` clears the old targets). A page with duplicated DOM `id`
attributes keeps the first occurrence — the same first-wins rule the parser and the reconciler
apply to bridge ids.

## Cadence and caps

A full snapshot is posted:

- on `DOMContentLoaded` and on `load` (the script itself runs before the body exists);
- debounced 150 ms behind `MutationObserver` bursts (childList/subtree/attributes);
- rAF-throttled on scroll, resize and wheel (one post per frame at most — this is what makes a
  scroll or a zoom repost instead of streaming).

The bridge caps every snapshot at **400 elements** (`BrowseElementScript.MaxElements`): a
runaway page cannot flood the dwell registry. The reconciler's move rule (T1: displacement of
either axis beyond 24 physical px re-registers the element fresh and discards its accumulated
dwell; smaller drift rebases in place and preserves it) lives in Core, not in the script.

## App → page: the dwell cue

The app posts `{"type":"dwell","id":...,"p":0..1}` (via `PostWebMessageAsJson`) as the dwell
progress changes: `p > 0` draws a pointer-events-none accent outline over the element's current
rect (the script re-reads the rect each draw, so it tracks scrolling); `p ≤ 0` — or an unknown
id — clears it. The payload is serialized with System.Text.Json because the id originates from
the page.

## Privacy: the bridge is content-free

Ids, coarse kinds and rects only. **No element text, no labels, no URLs, no attribute values
ever cross the bridge in either direction** — a text plus the layout would spell the page's
content, and the KB-E1 keyboard standard (content-free log lines) applies here unchanged.
Unknown fields anywhere in the envelope are ignored, never logged.

## Single-file publish note

The repo's shipped artifact is a `PublishSingleFile` self-contained exe (AGENTS.md, build &
run):

```
dotnet publish src/EyeCtrl.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Under that publish mode the WebView2 native loader (`WebView2Loader.dll`) is **not** loose beside
the exe — the T4 publish smoke produced 35 output files (the app, its pdbs/xml, the loose
resource files) and no WebView2 dll; the loader ships inside the bundle and is extracted at
runtime by the .NET single-file bundler. **The loader's behavior in that mode is verified only
by T4's smoke test, which ran the publish but did not launch or summon** — the summon path lands
with the browse coordinator (T5) and launching the real overlay app is deliberately gated behind
`EYECTRL_UI_TESTS=1` (AGENTS.md: it seizes the OS cursor from a gaze-only user). When filing
issues, distinguish the two failure classes `WebView2BrowseHost.Describe` separates:

| Symptom | Cause | User-facing notice |
|---|---|---|
| `Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException` during environment creation | the WebView2 **Evergreen runtime is not installed** on the machine | "The WebView2 Evergreen runtime is not installed." |
| `DllNotFoundException` / `FileNotFoundException` while constructing the host | the **native loader failed to load** under single-file (corrupt extraction, blocked temp dir, stripped bundle) | "The WebView2 native loader (WebView2Loader) failed to load." |

Either way the coordinator logs the reason, shows it in the browser's address bar and closes the
window — a missing runtime degrades, it never crashes startup (the host is constructed only on
first summon). Reproduction tip: a machine without the Evergreen runtime fails at environment
creation before any loader question; a loader failure reproduces on a machine that HAS the
runtime, only under the single-file publish.

## Deferred in v1 (recorded verbatim from the plan)

- tabs
- history / bookmarks / downloads / favorites
- cross-origin iframes and shadow DOM beyond one same-origin hop
- native select-dropdown option navigation
- a dedicated dwell-time settings knob (targets carry `ToolbarButtonDwellMs`)
- external-UIA fallback for non-WebView2 apps
- settings-UI rows
