# German localization — release notes

One file for the whole localization work; each ticket appends its section under its ticket id.

## T02 — literal ratchet, explicit number formats, host-supplied control text

Two user-visible changes land with T02, the foundation step. Both are corrections: an English
screen used to borrow the machine's locale for its numbers.

- **Numbers no longer follow a German PC's locale.** An English screen on a de-DE machine shows
  **"12.3 px"** where it showed **"12,3 px"**. The same applies to the calibration error/quality
  figures, the model download sizes, the eye-care summary and the track-status distance. A German
  screen keeps German formatting, and switches to it only when its surface's German is switched on.

- **The "Last calibration" date is unambiguous while Settings renders English.** It reads
  **"2026-10-08 14:03"** (year-month-day), so a German reader cannot mistake it for the 10th of
  August. Once the Settings surface renders German it shows **"08.10.2026 14:03"**, that culture's
  own general date/time order. The user chose the unambiguous English form on 2026-10-08.

## T11 - Step 1: the crisis and caretaker screens

The first German anyone sees. On German Windows, EyeCtrl now starts in German; the screens that
matter in a crisis, and the way back out of German, ship together.

- **German is the default on German Windows.** The Supervisor's tray, setup window and FallenBack
  dialog, the caregiver alert, the drift notice, the startup-error box, the TrackStatus window and
  the App's tray menu all follow the Windows language.
- **These screens stay English until T12/T13:** Settings, calibration, eye care and the on-screen
  overlays. So the German tray's "Einstellungen" opens the still-English Settings, where the
  language row's description is the already-German sentence under "Sprache / Language" ("Legt fest,
  in welcher Sprache EyeCtrl seine eigenen Fenster anzeigt."); every other row on that page is
  English. (**Settings switched in the T12 section below; calibration, eye care and the overlays
  are still English.**)
- **The way back is "Sprache / Language"**: the flat item in the App's tray menu, or the first row
  on General in Settings. The choice row shows each language's own name ("System", "Deutsch",
  "English"), so it reads whichever language is running; the Supervisor's setup window carries the
  same row when EyeCtrl itself will not start.
- **The English UI on a de-DE machine shows "12.3 px"**, not "12,3 px" (T02's number-format fix,
  in its section above).
- **Known until T12:** the restart progress and failure notices appear on every restart, including
  Settings' "Restart now" and a theme change, so the still-English Settings can show a German
  "EyeCtrl wird neu gestartet." notice. By design they speak the TARGET language: choosing Deutsch
  while English runs prompts in German, and choosing English from the German screen prompts in
  English. T12 rechecks this (N8) — **rechecked in the T12 section: with Settings German the
  notice is no longer a mixed-language screen.**

## T12 — Step 2: Settings

Settings now follows the Windows language like the Step-1 screens. The switch is one surface
(`settings`), so the sidebar, every page, every row and description, the confirmations, the enum
combos, the ring preview and the quick-action picker all turn German together.

- **German Settings on German Windows.** The sidebar reads "Allgemein", "Kurzbefehle",
  "Augenschutz", and every page is re-paginated to fit German text. The sidebar tab labels keep
  their geometry — the two longest, "Kurzbefehle" and "Augenschutz", stay on one line and hold
  their English boxes (`ShippedGermanSettingsTests`, `tab156`).
- **Every Settings row stays reachable.** German re-pagination was checked against the live
  content budget on the whole tab set: no page is empty and every dwell target stays inside the
  content cell (`ShippedGermanSettingsTests.EverySettingsPage_InTheShippedGerman_PaginatesAndStaysReachable`).
- **The learned page positions change once.** German rows are taller on some tabs, so pages hold
  different rows than before. This is the one-time cost of switching the surface; the arrangement
  is stable afterwards.
- **Saved values do not change.** Every combo still stores the same value code it did in English —
  the language row keeps saving `system`/`german`/`english`, never the endonyms
  (`ShippedGermanSettingsTests.EveryCombo_SavesTheSameCodes_UnderTheShippedGerman`).
- **Quick-action presets are German in Settings.** The picker, the list captions and the delete
  confirmation read the German preset names, while a newly added tile still stores the preset's
  English label and its stable id, for all three preset families
  (`ShippedGermanSettingsTests.EveryPresetPicker_InTheShippedGerman_...`, `...ThePresetListCaptionAndDeleteModal_...`).
- **The recovery path still works from the German screen.** The language row is still the first
  row of General page 1, still 3 dwells from the toolbar and 0 from the tray item
  (`ShippedGermanSettingsTests.FromTheToolbarSettingsButton_...`).
- **Settings' numbers and the "Last calibration" date now use German formats.** The T02 number-format
  fix promised this "once the Settings surface renders German"; this is that moment: a German
  Settings shows "12,3 px" and "08.10.2026 14:03" where the English surface showed "12.3 px" and
  the sortable "2026-10-08 14:03". Every Settings number reads through
  `StringTable.Current.FormatProviderFor("settings")`, now de-DE (the routing pin in
  `ShippedGermanSwitchOnTests`; the date itself in `CalibrationDateTextTests.GermanSettings_...`).
- **N8 rechecked (the T11 note):** the restart notices speak the TARGET language and appear on
  every restart. With Settings German the T11 mixed state — a German "EyeCtrl wird neu gestartet."
  on an English Settings — is gone: a German session's Settings and its notices are both German, and
  an English session's are both English — except while switching language, when the notices speak
  the target being switched to by design (`ShippedLanguageRecoveryTests`; `RestartNotices` and
  `SettingsWindow.TargetLanguage()`, `SettingsWindow.xaml.cs:1008`).
- **Accepted for the window until T13 (D3):** the Settings ring preview speaks German while the
  live ring still speaks English, and the quick-action picker speaks German while the grid still
  speaks English. The pairing is by design — one key per surface — and closes when Step 3 turns
  `overlays` on.
- **Still English until T13:** the calibration window and the gaze-offset probe, eye care, and the
  on-screen overlays including the live ring and the quick-actions grid. The Calibration *tab*
  is German; starting a calibration or the offset probe ("Jetzt kalibrieren", "Blickversatz
  messen") from German Settings opens the still-English calibration window.
- **Exception details stay English**, the same convention as the startup box's "Technische
  Details (Englisch)": a failed model download shows German "Download fehlgeschlagen: {0}" with the
  raw error in the `{0}` (`ModelsPageSource.cs`, the `settings.models.download.failed_detail` path).

## T13 — Step 3: calibration, eye care and the overlays

The gaze surfaces go German. With `calibration`, `eye-care` and `overlays` in the manifest, **every
surface of EyeCtrl follows the Windows language**; nothing user-facing stays English by design.

- **The live ring speaks German.** The action ring's labels ("Linksklick", "Rechtsklick", "2× Klick",
  "Ziehen", "Mehr", "Zurück"), the modifier bar ("Shift", "Strg", "Alt") and the ring segments'
  accessible names all follow the language (`ShippedGermanStep3Tests` — the view model's own labels and
  the A105/A68 fit on the shipped table; `GeometryInvarianceTests` builds every overlay in English and
  in the shipped German at 2.0 and finds identical dwell ids, centres and radii).
- **The D3 window closes.** Until T12 the Settings ring preview spoke German while the live ring stayed
  English; the quick-action picker spoke German while the grid stayed English. Both pairs now read the
  same German: the live ring's labels equal the preview's (the `mirrors:` pairs,
  `MirrorParityTests`), and the grid resolves its preset tiles through the `overlays` keys
  (`ShippedGermanStep3Tests.TheQuickActionsGrid_…` — "Kopieren", "Wiedergabe / Pause", "Fenster
  minimieren"). A quick-action tile the user named themselves keeps its own text (D2, unchanged).
- **Calibration is German.** The progress line ("Auf den Punkt blicken · 1 / 9"), the
  Retry/Accept buttons ("Wiederholen" / "So übernehmen"), the Esc and auto-dismiss hints and every
  failure sentence (typed since T03c; wrapped since T10) (`ShippedGermanStep3Tests`, and the shipped
  cases of `GermanGateTests.TheCalibrationButtons_…` / `EveryCalibrationFailure_…`).
- **Eye care is German.** The banner ("Zeit für eine Augenpause", "starten", "später"), the exercise
  screen ("Ende ✕", "Gleitendes Folgen") and the summary card ("✓ Gut gemacht", "Genauigkeit", "später"
  numbers in German formats) (`ShippedGermanStep3Tests` — the A56/card fit and the windows' captions).
- **The overlays' hints and names are German.** "Mit Esc beenden" (backdrop), "Mit Esc das Scrollen
  beenden" (scroll overlay), the pinned note's caption and ✕ ✎ ↕ names, the toolbar's UIA names and
  the icon-only edge buttons' names (`ShippedGermanStep3Tests.EveryIconOnlyTarget_…`).
- **AutomationIds never change with the language.** UI tests locate targets by AutomationId and keep
  running with English pinned; under German the toolbar and Settings tab faces keep the same ids
  (`ShippedGermanStep3Tests.TheToolbarsAndTabFaces_…`, `StringTableIsolationTests`).
- **Still English, deliberately:** exception details and technical messages — the startup box's
  "Technische Details (Englisch)", the recovery frame's "(Englisch)", a failed model download's `{0}` —
  and the logs. The product names ("EyeCtrl", "Tobii", "Supervisor", "Shift", "Alt") stay as they are.
- **The "Double click" ring exception is still open.** "Double click" measures +1.8 DIP against the
  +2 DIP ring rule; T13 re-measured the alternatives (German "2× Klick" +14.7 DIP; the Settings row's
  "Doppelklick" would measure +0.85 DIP on the ring) and left the decision — keep the recorded
  exception, lower the threshold, or reword the English — to the owner
  (`FitClassTests.EnglishExceptions` and `ShippedGermanStep3Tests.TheDoubleClickCandidates_…`).
