# Release notes — T02 visible changes (draft)

Two user-visible changes land with T02, the foundation step of the German localization work. Both
are corrections: an English screen used to borrow the machine's locale for its numbers.

- **Numbers no longer follow a German PC's locale.** An English screen on a de-DE machine shows
  **"12.3 px"** where it showed **"12,3 px"**. The same applies to the calibration error/quality
  figures, the model download sizes, the eye-care summary and the track-status distance. A German
  screen keeps German formatting, and switches to it only when its surface's German is switched on.

- **The "Last calibration" date changes shape on that machine.** The English screen used to render
  it with the machine's short date, **"08.10.2026 14:03"**; it now renders the English short date,
  month first: **"10/08/2026 14:03"**. A German reader can misread that as 10 August. The decision
  between accepting the month-first order for the English phase and using an unambiguous
  **"2026-10-08 14:03"** is pending; the code keeps the English short date until that call is made,
  and this note is updated with the outcome.
