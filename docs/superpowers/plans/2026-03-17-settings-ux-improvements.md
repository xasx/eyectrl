# Settings UX Improvements Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace all `AddCombo`/`AddRadio` controls in the settings overlay with a horizontal button list, and hide the cursor circle while settings is open.

**Architecture:** Two focused changes to two existing files. `AddHorizontalList` is a new private method added to `SettingsWindow.xaml.cs` replacing `AddCombo` and `AddRadio`. A `settingsOpen` guard variable is added to the closure scope in `App.xaml.cs` to suppress cursor circle visibility while settings is shown.

**Tech Stack:** C# / .NET 10 / WPF, `System.Windows.Controls.WrapPanel`, existing `DwellDetector`/`DwellTarget` infrastructure.

---

## Chunk 1: AddHorizontalList in SettingsWindow

### Task 1: Add `AddHorizontalList` method

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` (after the existing `AddCombo` method, around line 637)

- [ ] **Step 1: Add the new `AddHorizontalList` method**

  Insert the following two methods immediately after the closing `}` of `AddCombo` (before `AddToggle`):

  ```csharp
  private void AddHorizontalList(string idPrefix, string label, string[] options,
      Func<string> get, Action<string> set)
      => AddHorizontalList(idPrefix, label, options, options, get, set);

  private void AddHorizontalList(
      string idPrefix,
      string label,
      string[] displayOptions,
      string[] codeOptions,
      Func<string> getCode,
      Action<string> setCode)
  {
      AddLabel(label);

      var borders = new List<Border>();
      var texts = new List<TextBlock>();

      var wrapPanel = new WrapPanel
      {
          Orientation = Orientation.Horizontal,
          Margin = new Thickness(0, 2, 0, 12)
      };

      void Refresh()
      {
          var active = getCode();
          for (int j = 0; j < borders.Count; j++)
          {
              bool selected = j < codeOptions.Length && codeOptions[j] == active;
              borders[j].Background = new SolidColorBrush(selected
                  ? Color.FromArgb(200, 30, 100, 30)
                  : BtnBase);
              if (j < texts.Count)
                  texts[j].Text = (selected ? "\u25C9 " : "\u25CB ") + displayOptions[j];
          }
      }

      for (int i = 0; i < displayOptions.Length; i++)
      {
          var capturedCode = codeOptions[i];
          var id = $"{idPrefix}_{i}";

          var text = new TextBlock
          {
              Foreground = Brushes.White,
              FontSize = 22,
              FontWeight = FontWeights.SemiBold,
              VerticalAlignment = VerticalAlignment.Center
          };

          System.Windows.Automation.AutomationProperties.SetAutomationId(
              text, $"Settings_HList_{id}");

          var border = new Border
          {
              Height = 70,
              Margin = new Thickness(0, 0, 8, 8),
              Padding = new Thickness(20, 0, 20, 0),
              CornerRadius = new CornerRadius(8),
              MinWidth = 150,
              Cursor = System.Windows.Input.Cursors.Hand,
              Child = text
          };

          _controlActions[id] = () =>
          {
              setCode(capturedCode);
              Refresh();
          };

          border.MouseLeftButtonDown += (_, _) => _controlActions[id]();
          _controlBorders[id] = border;
          _controlDwell.AddTarget(new DwellTarget(
              id,
              new ScreenPoint(0, 0),
              DpiHelper.LogicalToPhysicalX(ControlBtnRadius),
              _dwellMs));

          borders.Add(border);
          texts.Add(text);
          wrapPanel.Children.Add(border);
      }

      ContentPanel.Children.Add(wrapPanel);
      Refresh();
  }
  ```

  Note: `WrapPanel` is in `System.Windows.Controls` — already imported. `◉` = `\u25C9`, `◎` = `\u25CB` (matching the existing `AddRadio` symbols).

- [ ] **Step 2: Build to verify it compiles**

  ```
  dotnet build EyeCtrl.slnx
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

  ```bash
  git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
  git commit -m "feat: add AddHorizontalList control builder to SettingsWindow"
  ```

---

### Task 2: Migrate the 6 call sites

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

- [ ] **Step 1: Replace `c_filter` call site**

  In `BuildCursorPages()`, replace:
  ```csharp
  () => AddCombo("c_filter", "Filter Mode",
      Enum.GetNames<GazeFilterMode>(),
      () => _settings.Cursor.FilterMode.ToString(),
      v => { _settings.Cursor.FilterMode = Enum.Parse<GazeFilterMode>(v); ApplyAndSave(); }),
  ```
  With:
  ```csharp
  () => AddHorizontalList("c_filter", "Filter Mode",
      Enum.GetNames<GazeFilterMode>(),
      () => _settings.Cursor.FilterMode.ToString(),
      v => { _settings.Cursor.FilterMode = Enum.Parse<GazeFilterMode>(v); ApplyAndSave(); }),
  ```

- [ ] **Step 2: Replace `k_layout` call site**

  In `BuildKeyboardPages()`, replace:
  ```csharp
  () => AddCombo("k_layout", "Active Layout",
      ["QWERTY", "QWERTZ", "AZERTY"],
      () => _settings.Keyboard.ActiveLayout,
      v => { _settings.Keyboard.ActiveLayout = v; ApplyAndSave(); }),
  ```
  With:
  ```csharp
  () => AddHorizontalList("k_layout", "Active Layout",
      ["QWERTY", "QWERTZ", "AZERTY"],
      () => _settings.Keyboard.ActiveLayout,
      v => { _settings.Keyboard.ActiveLayout = v; ApplyAndSave(); }),
  ```

- [ ] **Step 3: Replace `k_edge` call site**

  In `BuildKeyboardPages()`, replace:
  ```csharp
  () => AddCombo("k_edge", "Screen Edge", ["Bottom", "Top"],
      () => _settings.Keyboard.ScreenEdge,
      v => { _settings.Keyboard.ScreenEdge = v; ApplyAndSave(); }),
  ```
  With:
  ```csharp
  () => AddHorizontalList("k_edge", "Screen Edge",
      ["Bottom", "Top"],
      () => _settings.Keyboard.ScreenEdge,
      v => { _settings.Keyboard.ScreenEdge = v; ApplyAndSave(); }),
  ```

- [ ] **Step 4: Replace `t_edge` call site**

  In `BuildToolbarPages()`, replace:
  ```csharp
  () => AddCombo("t_edge", "Docked Edge", ["Top", "Bottom", "Left", "Right"],
      () => _settings.Toolbar.DockedEdge,
      v => { _settings.Toolbar.DockedEdge = v; ApplyAndSave(); }),
  ```
  With:
  ```csharp
  () => AddHorizontalList("t_edge", "Docked Edge",
      ["Top", "Bottom", "Left", "Right"],
      () => _settings.Toolbar.DockedEdge,
      v => { _settings.Toolbar.DockedEdge = v; ApplyAndSave(); }),
  ```

- [ ] **Step 5: Replace `th_mode` call site**

  In `BuildThemePages()`, replace:
  ```csharp
  () => AddCombo("th_mode", "Theme Mode", ["System", "Light", "Dark"],
      () => _settings.Theme.Mode,
      v => { _settings.Theme.Mode = v; ApplyAndSave(); }),
  ```
  With:
  ```csharp
  () => AddHorizontalList("th_mode", "Theme Mode",
      ["System", "Light", "Dark"],
      () => _settings.Theme.Mode,
      v => { _settings.Theme.Mode = v; ApplyAndSave(); }),
  ```

- [ ] **Step 6: Replace `k_lang` call site (was `AddRadio`)**

  In `BuildKeyboardPages()`, replace:
  ```csharp
  () => AddRadio("k_lang", "Dictionary Language",
      displayOptions: ["English", "French", "Spanish", "German", "Polish"],
      codeOptions:    ["en",      "fr",     "es",      "de",     "pl"],
      getCode: () => string.IsNullOrEmpty(_settings.Keyboard.LanguagePack) ? "en"
                     : _settings.Keyboard.LanguagePack,
      setCode: v => { _settings.Keyboard.LanguagePack = v; ApplyAndSave(); }),
  ```
  With:
  ```csharp
  () => AddHorizontalList("k_lang", "Dictionary Language",
      displayOptions: ["English", "French", "Spanish", "German", "Polish"],
      codeOptions:    ["en",      "fr",     "es",      "de",     "pl"],
      getCode: () => string.IsNullOrEmpty(_settings.Keyboard.LanguagePack) ? "en"
                     : _settings.Keyboard.LanguagePack,
      setCode: v => { _settings.Keyboard.LanguagePack = v; ApplyAndSave(); }),
  ```

- [ ] **Step 7: Build to verify all call sites compile**

  ```
  dotnet build EyeCtrl.slnx
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 8: Commit**

  ```bash
  git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
  git commit -m "feat: migrate all AddCombo/AddRadio call sites to AddHorizontalList"
  ```

---

### Task 3: Delete `AddCombo` and `AddRadio`

**Files:**
- Modify: `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs`

- [ ] **Step 1: Delete the `AddCombo` method**

  Remove the entire `private void AddCombo(...)` method block (approximately lines 638–678 before edits). It starts with:
  ```csharp
  private void AddCombo(string id, string label, string[] options,
      Func<string> get, Action<string> set)
  {
  ```
  And ends at its closing `}`.

- [ ] **Step 2: Delete the `AddRadio` method**

  Remove the entire `private void AddRadio(...)` method block. It starts with:
  ```csharp
  private void AddRadio(
      string idPrefix,
      string label,
  ```
  And ends at its closing `}` (after the initial-highlight loop).

- [ ] **Step 3: Build to verify no remaining usages**

  ```
  dotnet build EyeCtrl.slnx
  ```

  Expected: `Build succeeded. 0 Error(s)`

  If the build reports errors about `AddCombo` or `AddRadio` still being called, a call site was missed — find and replace it before continuing.

- [ ] **Step 4: Commit**

  ```bash
  git add src/EyeCtrl.App/Views/SettingsWindow.xaml.cs
  git commit -m "refactor: remove obsolete AddCombo and AddRadio methods"
  ```

---

## Chunk 2: Cursor circle hidden while settings open

### Task 4: Add `settingsOpen` guard to `App.xaml.cs`

**Files:**
- Modify: `src/EyeCtrl.App/App.xaml.cs`

The settings window creation logic lives inside a `messenger.Register<SettingsRequestedMessage>` lambda. The `cursorVM.PropertyChanged` handler is defined earlier in the same method. Both capture variables from the same closure scope, so a local `bool settingsOpen` is shared between them.

> **Naming note:** The spec refers to this as `_settingsOpen` (field convention), but because all the relevant code lives inside a single method in `App.xaml.cs`, a closure-captured local variable is sufficient and avoids adding a field to the class for a concern that is entirely internal to that method's lifetime.

- [ ] **Step 1: Add `settingsOpen` local variable**

  Find the line where `cursorVM.PropertyChanged` is first assigned (looks like this):
  ```csharp
  // CursorCircle: VM.Position -> window position, VM.IsVisible -> show/hide
  cursorVM.PropertyChanged += (_, e) =>
  ```

  Immediately **before** this line, add:
  ```csharp
  bool settingsOpen = false;
  ```

- [ ] **Step 2: Modify the `cursorVM.PropertyChanged` `IsVisible` case**

  Change:
  ```csharp
  case nameof(CursorCircleViewModel.IsVisible):
      if (cursorVM.IsVisible) cursorCircle.Show();
      else cursorCircle.Hide();
      break;
  ```
  To:
  ```csharp
  case nameof(CursorCircleViewModel.IsVisible):
      if (cursorVM.IsVisible && !settingsOpen) cursorCircle.Show();
      else cursorCircle.Hide();
      break;
  ```

- [ ] **Step 3: Modify the settings window creation block**

  Find:
  ```csharp
  SettingsWindow? settingsWindow = null;
  messenger.Register<SettingsRequestedMessage>(this, (_, _) => Dispatcher.BeginInvoke(() =>
  {
      if (settingsWindow != null) return;
      settingsWindow = new SettingsWindow(settings, settingsService);
      settingsWindow.SettingsChanged += s =>
      {
          cursorCircle.UpdateAppearance(s.Cursor);
          toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
          toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);
          im.ZoomEnabled = s.Zoom.Enabled;
          toolbar.SetZoomState(s.Zoom.Enabled);
          LoadKeyboard(keyboard, s, inputSimulator, settingsService);
          Log.Information("Settings saved");
      };
      settingsWindow.Closed += (_, _) => settingsWindow = null;
      settingsWindow.Show();
  }));
  ```

  Replace with:
  ```csharp
  SettingsWindow? settingsWindow = null;
  messenger.Register<SettingsRequestedMessage>(this, (_, _) => Dispatcher.BeginInvoke(() =>
  {
      if (settingsWindow != null) return;
      settingsOpen = true;
      cursorCircle.Hide();
      settingsWindow = new SettingsWindow(settings, settingsService);
      settingsWindow.SettingsChanged += s =>
      {
          cursorCircle.UpdateAppearance(s.Cursor);
          toolbar.Initialize(s.Toolbar.Size, s.Dwell.ToolbarButtonDwellMs);
          toolbar.SetMode(s.Toolbar.IsDocked, s.Toolbar.DockedEdge);
          im.ZoomEnabled = s.Zoom.Enabled;
          toolbar.SetZoomState(s.Zoom.Enabled);
          LoadKeyboard(keyboard, s, inputSimulator, settingsService);
          Log.Information("Settings saved");
      };
      settingsWindow.Closed += (_, _) =>
      {
          settingsWindow = null;
          settingsOpen = false;
          if (cursorVM.IsVisible) cursorCircle.Show();
      };
      settingsWindow.Show();
  }));
  ```

- [ ] **Step 4: Build to verify**

  ```
  dotnet build EyeCtrl.slnx
  ```

  Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

  ```bash
  git add src/EyeCtrl.App/App.xaml.cs
  git commit -m "feat: hide cursor circle while settings window is open"
  ```

---

## Manual Verification Checklist

After all tasks are complete, run the app and verify:

- [ ] Open Settings (⚙ toolbar button). The cursor circle disappears immediately.
- [ ] While Settings is open, move your gaze around the screen — confirm the cursor circle stays hidden throughout (verifies the `settingsOpen` guard holds even as gaze data keeps arriving).
- [ ] Close Settings. The cursor circle reappears (if gaze is active).
- [ ] In Settings → Cursor tab: "Filter Mode" shows as a row of horizontal buttons (e.g. `◉ OneEuro  ◎ Kalman  ◎ Adaptive`). Dwelling activates the chosen option.
- [ ] In Settings → Keyboard tab: "Active Layout" shows as horizontal buttons (`◉ QWERTY  ◎ QWERTZ  ◎ AZERTY`). "Screen Edge" shows `◎ Bottom  ◉ Top` (or similar). "Dictionary Language" shows all 5 languages horizontally.
- [ ] In Settings → Toolbar tab: "Docked Edge" shows horizontal buttons.
- [ ] In Settings → Theme tab: "Theme Mode" shows horizontal buttons.
- [ ] Selecting a different option in any horizontal list updates the highlight correctly (green = selected, grey = unselected).
- [ ] Dwell progress animation works on horizontal list buttons (brightness increases during dwell).
