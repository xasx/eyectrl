# EyeCtrl — User Guide

EyeCtrl is gaze control: you look at a target, a ring fills, and the action fires. This guide
covers the gaze browser added by the browse plan (v1). For everything else — the ring, the
keyboard, the toolbar — see the in-app Settings pages, which describe their own controls.

---

## The Gaze Browser

The gaze browser is a single-tab web browser window built for gaze use. Its pages are read
normally, but every interactive element — links, buttons, text fields, dropdowns — becomes a
dwell target automatically: **look at it until the ring completes, and the browser clicks it.**

### Summoning

Open the ring's second layer (**More**), then dwell the **Browse** slot. The browser opens over
the work area with a start page. Dwelling Browse again while it is open brings it to the front —
the ring action never closes it. While the browser is still settling in (it has not passed its
first end-to-end check yet), it ships **off the ring by default**: turn it on once in Settings →
Action menu → visible actions (**Browse**), and later builds will offer it on the ring on their own.

### What stays above the browser

The browser is a normal window in EyeCtrl's overlay stack: **the toolbar band and every surface
you summon later — the ring, the gaze keyboard, the edge bars, the task switcher — appear above
it.** The browser never covers them. If the toolbar was visible when the browser opened, it is
re-asserted above the browser automatically. While one of those surfaces is the one under your
gaze, the browser underneath does not accumulate a click — looking at the keyboard to type can
never fire a page link hiding behind it.

### The address bar

The chrome row at the top holds Back, Forward, Home, Reload, the address field and Close.

To type an address: dwell the **address box** — it takes real keyboard focus — then summon the
gaze keyboard (ring → Keyboard) and type, or use a physical keyboard. **Enter** navigates. A
bare name like `example.com` gets `https://` automatically.

### Page interaction

Links, buttons and fields on the page become dwell targets on their own — no setup, no re-scan
delay beyond the page's own loading. While a dwell is in progress, the page itself draws a small
accent outline around the target so you can see what you are about to click. Scrolling the page
with a dwell resting on an element never completes that dwell: the element must still be where
you looked. A link that asks for a new window opens in this tab instead — the browser never
spawns popups.

### Scrolling

Scrolling reuses EyeCtrl's existing scroll machinery — the ring's scroll actions and **Scroll
Mode** (the ring's scroll-mode slot) work over the browser exactly as over anything else. While
Scroll Mode is active, page-element dwell is suspended, so a page sliding under your gaze cannot
fire a click.

### The chrome controls

- **Back / Forward** — history.
- **Home** — back to the start page.
- **Reload** — re-fetch the current page.
- **Close** — tears the browser down completely (its window close does the same).

### While paused

Pause keeps its usual rule — no input leaves EyeCtrl. In the browser that means: **only Close
still works** (it is the way out), while Back, Forward, Home, Reload, the address box and page
elements do nothing until you unpause — a paused gaze must not be able to reload a page and drop
what you typed into it.

### If the browser won't open

The browser needs Microsoft's WebView2 runtime, which is preinstalled on up-to-date Windows. If
it is missing (or a browser component fails to load), the browser shows the reason in the
address bar in plain language and **stays open** — Close is the exit, nothing crashes. Follow
the notice (it names what to do: have a helper install the
[WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) or repair the
installation) and close and reopen the browser.

### Not in v1

A single tab only — no tabs, no history/bookmarks/downloads/favorites. Frames (iframes) of any
origin are not enumerated — their controls are mouse-only for now; shadow DOM likewise. Popup
windows, script dialogs, download and permission prompts are not gaze-operable. Elements that
are visually present but inert (disabled controls, non-clickable decorations that merely listen
to JavaScript without a role) are not dwell targets. Native select dropdowns open, but their
option lists are not gaze-navigable. The browser's dwell time follows the toolbar button
setting; there is no dedicated knob yet, and the browser has no Settings-page rows of its own.
