namespace EyeCtrl.Core.Localization;

using System.Collections.Generic;

/// <summary>
/// The closed enums of the string-table contract, in C#. The Python validator holds the same sets
/// and runs the gates over them; <c>StringsEnumParityTests</c> parses that file and fails when the
/// two drift, so changing a set in one language without the other is red rather than silently
/// divergent.
/// </summary>
public static class StringTableSchema
{
    /// <summary>Surface ids a key's <c>where</c> may name; a key's prefix must equal its surface.</summary>
    public static IReadOnlyList<string> Surfaces { get; } =
    [
        "supervisor",
        "app-tray",
        "caregiver-alert",
        "drift-notice",
        "startup-error",
        "track-status",
        "language-recovery",
        "settings",
        "calibration",
        "eye-care",
        "overlays"
    ];

    /// <summary>Audience values the <c>who</c> meta field may carry.</summary>
    public static IReadOnlyList<string> Audiences { get; } = ["caretaker", "user", "both"];

    /// <summary>
    /// Fit classes the <c>fit</c> meta field may carry (A-modes is dropped, D8).
    ///
    /// <para>
    /// A class names the geometry a key's text must fit, and T09's fit harness
    /// (<c>tests/EyeCtrl.App.Tests/Localization/Fit</c>, plus the Supervisor's classes in
    /// <c>EyeCtrl.Supervisor.Tests</c>) measures every key in its class's PRODUCTION control at
    /// scale 2.0. Some names are historical and do not state the box they measure; the comment on
    /// each says what is actually measured, so a translator budgeting by the name is not misled.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FitClasses { get; } =
    [
        // The action-menu ring label: one line whose ink box stays ≥ 2 DIP inside the progress
        // arc of a radius-70 ring button (the ~104.7 DIP chord), clear of the icon.
        "A105",
        // The Shift/Ctrl/Alt modifier bar (D8): the same ink rule at radius 50.
        "A68",
        // A toggle's On/Off state word: one line in the 44 DIP left of the switch's track.
        "A44",
        // A short caption: one line ≤ 56 DIP (the eye-care banner's 64 DIP rings less 8).
        "A56",
        // The settings mode toggle's label: one line inside the toggle's box less the standard
        // 8-DIP one-line inset — i.e. <= 208 - 8. 208 derives from 448 (narrowest header) -
        // 220.2 (tab156's widest English title at Display/Strong) - 12 (gap) - slack. The live
        // per-language title+toggle bound is pinned in SettingsContentFitTests, not restated here.
        "A208",
        // The eye-care summary card's labels: one line, and the auto-width card they widen still
        // fits the 1920 DIP exercise window (the card has no box of its own, so that is the bound).
        "card",
        // A Settings sidebar tab: the label may overhang its tab highlight by < 8 DIP.
        "tab156",
        // The Supervisor tray tooltip: prefix + phrase ≤ 63 characters (D12).
        "tray63",
        // A default 100×100 DwellButton: 94 × 94 DIP of wrapping text (no keys today).
        "B94x2",
        // The quick-actions tile label. NOT 114: the label wraps in 106 DIP (the 120 DIP tile less
        // its border and the label's 6 DIP margins; 104 under the Contrast theme's 2 DIP border,
        // which binds) and must fit the tile's height under the icon.
        "B114",
        // A 300×100 Settings button. NOT 260: the label wraps in 300 DIP and must fit 94 DIP of
        // height (the 6 DIP dwell bar is reserved), which is two lines.
        "B260x2",
        // The FallenBack dialog's 260×110 buttons: 260 × 104 DIP of wrapping text (three lines).
        "B260x3",
        // The FallenBack dialog's title and body: the window grows in height only, the button row
        // keeps its full height, and the grown window fits the 200 % work area.
        "C-fallback",
        // The caregiver alert: the 720 DIP window grows in height only, its bottom edge stays at
        // the work area's bottom less 48 DIP, and the grown window fits the work area.
        "C-alert",
        // A two-choice modal button: one line ≤ 170 − 8 DIP.
        "modal170",
        // A stacked (three-choice) modal button: one line ≤ 460 − 8 DIP.
        "modal460",
        // The adaptation-preview row (D15): direction word + right-aligned value in a ≤360 DIP
        // panel row, ink ≥ 2 DIP from the bar.
        "PV1",
        // Settings DwellButtons of a fixed W×H DIP box: the label wraps in W, and its laid-out
        // height must fit H − 6 (the dwell bar's row); no run may need a mid-word break.
        "btn360x100",
        "btn360x70",
        "btn380x100",
        "btn420x100",
        "btn420x70",
        "btn420x90",
        "btn440x100",
        "btn544x100",
        "btn544x106",
        // Paginated Settings rows and wrapping prose: pagination and reachability (T09 PR 2).
        "row",
        "prose"
    ];
}
