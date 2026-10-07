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

    /// <summary>Fit classes the <c>fit</c> meta field may carry (A-modes is dropped, D8).</summary>
    public static IReadOnlyList<string> FitClasses { get; } =
    [
        "A105",
        "A68",
        "A44",
        "A56",
        "tab156",
        "tray63",
        "B94x2",
        "B114",
        "B260x2",
        "B260x3",
        "C-fallback",
        "C-alert",
        "row",
        "prose"
    ];
}
