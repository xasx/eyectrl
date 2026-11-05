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
the ring action never closes it.

### What stays above the browser

The browser is a normal window in EyeCtrl's overlay stack: **the toolbar band and every surface
you summon later — the ring, the gaze keyboard, the edge bars, the task switcher — appear above
it.** The browser never covers them. If the toolbar was visible when the browser opened, it is
re-asserted above the browser automatically.

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
you looked.

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

Pause keeps its usual rule — no input leaves EyeCtrl. In the browser that means: **Back,
Forward, Home, Reload and Close still work** (they act on EyeCtrl's own window), but a dwell on
a page element does not click and dwelling the address box does not take focus. Unpause to
interact with the page.

### If the browser won't open

The browser needs Microsoft's WebView2 Evergreen runtime, which is preinstalled on up-to-date
Windows. If it is missing (or its loader fails to load), the browser shows the reason in the
address bar and closes instead of crashing the app. Install the [Evergreen runtime]
(https://developer.microsoft.com/microsoft-edge/webview2/) and summon the browser again.

### Not in v1

A single tab only — no tabs, no history/bookmarks/downloads/favorites. Same-origin frames are
enumerated one hop; cross-origin frames and shadow DOM are not. Native select dropdowns open,
but their option lists are not gaze-navigable. The browser's dwell time follows the toolbar
button setting; there is no dedicated knob yet, and the browser has no Settings-page rows.
