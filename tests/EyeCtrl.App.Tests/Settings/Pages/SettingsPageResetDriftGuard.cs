namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// The #157 drift-guard machinery, parameterized over a page source (spec §Settings). Kept as
/// one file so the perturb/read/kind-exemption logic has exactly one home. The spec-kind
/// reflection gate stays in the Keyboard test file (page-agnostic as written); the kind sets
/// are internal here so that file keeps compiling against them (B6).
/// </summary>
internal static class SettingsPageResetDriftGuard
{
    internal static void ResetRestoresEverySettingsRow(
        Func<SettingsPageContext, IReadOnlyList<PageSpec>> build,
        string resetButtonId, string pageName)
    {
        var expected = DefaultViewByRowId(build);

        // Perturb every settings row through its OWN setter, then run the reset via the
        // confirmed modal. A row whose setting the handler forgot stays perturbed and is
        // named in the failure — against the pre-fix handler that row was k_sound.
        var ctx = TestSettingsPageContext.Build(showModal: (_, act) => act());
        var rows = build(ctx).SelectMany(p => p.Rows).ToList();

        // A page with no settings rows would pass the guard vacuously (review round 2, N5),
        // and the empty-build shape is not hypothetical: a future row group gated on a
        // host-supplied service (the dictionary page's `if (dictionary is null) yield break`)
        // would empty the guard headlessly with no signal. A theory over an empty set proves
        // nothing (SettingsRowPitchTests).
        Check.WithCustomMessage(
            $"No settings rows were enumerated from the {pageName} page's Build() — the guard "
            + "would pass vacuously. Either the page stopped rendering settings rows or "
            + "IsSettingsRow went stale.")
            .That(rows.Where(IsSettingsRow).ToList()).Not.IsEmpty();

        foreach (var row in rows.Where(IsSettingsRow))
        {
            Perturb(row);
            // A row whose Set is dead or mis-wired reads its default before AND after the
            // reset, so the guard would pass vacuously for it (review round 1, finding 1: a
            // dead setter survived 9/9 green; round 2's N1 showed a mis-wired setter — writing
            // a property its Get does not read — is the same failure). Require the
            // perturbation to have actually landed before the reset is allowed to look correct.
            Check.WithCustomMessage(
                $"Perturbing '{row.Id}' on the {pageName} page did not change the value its own "
                + "Get reads — a dead or mis-wired setter, so this row's reset is not actually "
                + "under test.")
                .That(Read(row)).Not.IsEqualTo(expected[row.Id]);
        }

        rows.OfType<ButtonSpec>().Single(b => b.Id == resetButtonId).OnActivate();

        // Read back through a fresh Build — the same path RebuildContent takes in the window.
        var drifted = build(ctx).SelectMany(p => p.Rows)
            .Where(IsSettingsRow)
            .Select(r => (Id: r.Id, Got: Read(r)))
            .Where(x => !expected.TryGetValue(x.Id, out var def) || !Equals(x.Got, def))
            .Select(x => expected.TryGetValue(x.Id, out var def)
                ? $"{x.Id}: still {x.Got}, default {def}"
                : $"{x.Id}: present after reset but absent from a fresh default page build")
            .ToList();

        Check.WithCustomMessage(
            $"{resetButtonId} must restore every settings row on the {pageName} page to its " +
            "default; these rows kept their perturbed value (add the setting to the reset " +
            "handler): "
            + string.Join("; ", drifted))
            .That(drifted).IsEmpty();
    }

    /// <summary>
    /// The settings-bound spec kinds the guard can read and perturb. Membership is
    /// assignability — every spec kind is sealed today, so this is type equality in practice,
    /// but a subclass WOULD be covered automatically if a kind were ever unsealed (same
    /// semantics as the old `is` filter). Anything ELSE must appear in
    /// <see cref="NotASettingsKind"/> or the reflection gate in the Keyboard test file fails.
    /// </summary>
    internal static readonly HashSet<Type> SettingsKinds =
    [
        typeof(SliderSpec), typeof(ToggleSpec), typeof(ComboBoxSpec), typeof(ColorPickerSpec)
    ];

    /// <summary>Spec kinds deliberately outside <see cref="SettingsKinds"/>, keyed by FullName, each with the reason.</summary>
    internal static readonly Dictionary<string, string> NotASettingsKind = new(StringComparer.Ordinal)
    {
        [typeof(ButtonSpec).FullName!] = "action row — binds no setting to restore",
        [typeof(LabelSpec).FullName!] = "static text — binds no setting",
    };

    private static bool IsSettingsRow(SettingsRowSpec r) =>
        SettingsKinds.Any(k => k.IsInstanceOfType(r));

    /// <summary>The page's getter view over a fresh default settings object: row Id → value.</summary>
    private static Dictionary<string, object> DefaultViewByRowId(
        Func<SettingsPageContext, IReadOnlyList<PageSpec>> build) =>
        build(TestSettingsPageContext.Build())
            .SelectMany(p => p.Rows).Where(IsSettingsRow)
            .ToDictionary(r => r.Id, Read);

    private static object Read(SettingsRowSpec r) => r switch
    {
        ToggleSpec t => t.Get(),
        SliderSpec s => s.Get(),
        ComboBoxSpec c => c.Get(),
        ColorPickerSpec p => p.Get(),
        _ => throw new InvalidOperationException($"Unhandled settings-row kind: {r.GetType().Name} ({r.Id})"),
    };

    /// <summary>
    /// Drive the row's own setter to a value different from the current one — any value will
    /// do; the point is to make "reset forgot this row" observable. The slider case flips to
    /// the opposite end of the range so a default sitting at either extreme still moves.
    /// </summary>
    private static void Perturb(SettingsRowSpec r)
    {
        switch (r)
        {
            case ToggleSpec t:
                t.Set(!t.Get());
                break;
            case SliderSpec s:
                s.Set(Math.Abs(s.Get() - s.Max) < 1e-9 ? s.Min : s.Max);
                break;
            case ComboBoxSpec c:
            {
                var options = c.Code ?? c.Display;
                var candidate = options.FirstOrDefault(
                    o => !string.Equals(o, c.Get(), StringComparison.Ordinal));
                if (candidate is null)
                    throw new InvalidOperationException(
                        $"Cannot perturb '{c.Id}': no option differs from its current value "
                        + $"'{c.Get()}' (single-option combo, or a getter outside the option list).");
                c.Set(candidate);
                break;
            }
            case ColorPickerSpec p:
                p.Set(string.Equals(p.Get(), "#FF0000", StringComparison.OrdinalIgnoreCase)
                    ? "#00FF00" : "#FF0000");
                break;
            default:
                throw new InvalidOperationException($"Unhandled settings-row kind: {r.GetType().Name} ({r.Id})");
        }
    }
}
