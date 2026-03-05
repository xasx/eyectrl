# Provider Indicator Design

**Date:** 2026-03-05

## Overview

Add a read-only indicator to the toolbar showing which gaze input provider is currently active (Tobii hardware or mouse simulator). Temporary/session-only — no persistence, no switching.

## Design

**Visual:** A non-interactive `Border` prepended to the toolbar's `ButtonPanel` (left of the action buttons). Same dark background (`#B43C3C3C`) and corner radius as action buttons, same `_buttonSize` dimensions, no dwell target registered.

**Symbols:**
- Tobii active: `👁` (U+1F441)
- Mouse simulator active: `🖱` (U+1F5B1)

Both render via Segoe UI Emoji on Windows 10/11.

**New method on ToolbarWindow:**
```csharp
public void SetProviderIndicator(bool isTobii)
```

Called from `App.xaml.cs` after `toolbar.Initialize(...)`, once the provider is known.

## Files Changed

| File | Change |
|---|---|
| `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs` | Add `SetProviderIndicator(bool isTobii)` method |
| `src/EyeCtrl.App/App.xaml.cs` | Call `SetProviderIndicator` after provider selection |
