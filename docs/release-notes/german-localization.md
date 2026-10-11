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
- **The way back is "Sprache / Language"**: the flat item in the App's tray menu, or the first row
  on General in Settings. The choice row shows each language's own name ("System", "Deutsch",
  "English"), so it reads whichever language is running; the Supervisor's setup window carries the
  same row when EyeCtrl itself will not start.
- **The English UI on a de-DE machine shows "12.3 px"**, not "12,3 px" (T02's number-format fix,
  in its section above).
- **Known until T12:** the restart progress and failure notices appear on every restart, including
  Settings' "Restart now" and a theme change. Between this release and T12, the still-English
  Settings can therefore show a German "EyeCtrl wird neu gestartet." restart notice. T12 rechecks
  this (N8).
