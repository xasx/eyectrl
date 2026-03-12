# Activation Button Placement — Quadrant-Based Diagonal Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fixed vertical offset for the activation button with a quadrant-based diagonal placement that keeps the button on the opposite side of the gaze circle from the nearest screen corner.

**Architecture:** The positioning logic lives entirely inside `ActivationButtonWindow.ShowAt()`. It reads the gaze point, determines the screen quadrant, computes the button center at 45° in the direction away from the nearest corner, then clamps to screen bounds. The call site in `App.xaml.cs` requires no change.

**Tech Stack:** C# 12, .NET 8, WPF. Build: `dotnet build`. Tests: `dotnet test`.

**Spec:** `docs/superpowers/specs/2026-03-12-activation-button-placement-design.md`

---

## Chunk 1: Replace ShowAt positioning logic

### Task 1: Implement quadrant-diagonal placement in ActivationButtonWindow

**Files:**
- Modify: `src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs:19-24`

> **Note on TDD:** The `ShowAt` logic uses `SystemParameters` (static WPF API) and `Width`/`Height` (WPF dependency properties). These cannot be called in a headless xUnit test. The positioning math is trivial (4 comparisons + arithmetic) and is fully covered by the manual test steps below. No unit test is added for this change.

- [ ] **Step 1: Read the current file**

  ```
  src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs
  ```

  Confirm `ShowAt` currently reads:
  ```csharp
  public void ShowAt(ScreenPoint position, double verticalOffset = 180)
  {
      var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
      var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
      Left = Math.Clamp(position.X - Width / 2, 0, screenW - Width);
      Top  = Math.Clamp(position.Y - Height / 2 - verticalOffset, 0, screenH - Height);
      ...
  }
  ```

- [ ] **Step 2: Replace the ShowAt signature and positioning logic**

  Change the signature from `ShowAt(ScreenPoint position, double verticalOffset = 180)` to `ShowAt(ScreenPoint position, double offset)` and replace the positioning body with:

  ```csharp
  public void ShowAt(ScreenPoint position, double offset)
  {
      var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
      var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;

      var dx = position.X < screenW / 2 ? 1.0 : -1.0;
      var dy = position.Y < screenH / 2 ? 1.0 : -1.0;

      var buttonCenterX = position.X + dx * offset / Math.Sqrt(2);
      var buttonCenterY = position.Y + dy * offset / Math.Sqrt(2);

      Left = Math.Clamp(buttonCenterX - Width / 2, 0, screenW - Width);
      Top  = Math.Clamp(buttonCenterY - Height / 2, 0, screenH - Height);

      UpdateDwellProgress(0);

      var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
      BeginAnimation(OpacityProperty, fadeIn);
  }
  ```

  The rest of the method (UpdateDwellProgress, fadeIn) is unchanged — just move it inside the new body if it was already there.

- [ ] **Step 3: Verify App.xaml.cs call site needs no change**

  Search for `ShowAt` in `src/EyeCtrl.App/App.xaml.cs`. Confirm it reads:
  ```csharp
  activationButton.ShowAt(activationVM.Position, buttonOffset);
  ```
  This is a positional call — no change needed.

- [ ] **Step 4: Build to verify compilation**

  ```
  dotnet build src/EyeCtrl.App/EyeCtrl.App.csproj
  ```

  Expected: Build succeeded, 0 errors, 0 warnings (or only pre-existing warnings).

- [ ] **Step 5: Run tests to verify nothing regressed**

  ```
  dotnet test
  ```

  Expected: All tests pass (currently 30 passing).

- [ ] **Step 6: Commit**

  ```bash
  git add src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml.cs
  git commit -m "feat: place activation button diagonally opposite nearest screen corner"
  ```

---

## Manual Verification

Run the app and move your gaze to each screen quadrant. Confirm:

| Gaze area | Expected button position |
|---|---|
| Top-left of screen | Below and to the right of the circle |
| Top-right of screen | Below and to the left of the circle |
| Bottom-left of screen | Above and to the right of the circle |
| Bottom-right of screen | Above and to the left of the circle |

Also verify:
- Button does not visually overlap the cursor circle (~10px gap at defaults)
- Button stays fully within screen bounds when gazing near screen edges and at extreme corners (where clamping engages)
- Button still appears/disappears with fade animation
- Dwell progress arc still works
