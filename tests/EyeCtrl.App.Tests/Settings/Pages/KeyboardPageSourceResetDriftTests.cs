namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Drift guard for the Keyboard tab's "Reset Keyboard Settings" button (issue #157): every
/// settings row the page renders must come back to its default when k_reset is confirmed.
/// The handler writes a hand-listed field copy, and that list has drifted before —
/// ClickSoundEnabled had a row on the very page the button resets and was missed (issue
/// #157), and MaxSuggestions joined the reset in #160 without the transcription-era canary
/// test (which still says "exactly four fields") noticing. The rows here are enumerated from
/// <see cref="KeyboardPageSource.Build"/> — the same enumeration the description-completeness
/// gate uses — NOT from a hand-list of Ids: a new row joins this guard the moment it joins the
/// page, and the reset handler must then write its setting or this test fails naming the row.
/// That is the guard's declared design decision: reuse the page's own row specs, or the guard
/// just recreates the drift it guards against.
///
/// <para>Defaults are read through the page's own getters over a fresh
/// <see cref="EyeCtrl.Core.Settings.EyeCtrlSettings"/>, not hand-listed: the reset reads
/// <c>new KeyboardSettings()</c>, and this test reads the same construction through the same
/// getters — so getter-side mappings (k_lang's empty-pack → "en") cost nothing and a default
/// change never needs a test edit.</para>
///
/// <para>Stated limit (review round 1, finding 3): the comparison is over the rows' getter
/// VIEWS, so a reset value a getter-side mapping renders identically (k_lang writing "en"
/// instead of the default "" — both display "en") is invisible here. The per-field asserts in
/// <see cref="SettingsPageSourceCallbackTests"/> read the raw properties through a captured
/// reference and cover exactly that gap; the two tests compose deliberately.</para>
///
/// <para>Scope: the Keyboard tab only — k_reset is a per-tab reset. The app-wide reset
/// (SettingsViewModel.ResetAllSettings over EnumerateSubSettings) is a different code path.
/// Rows that are not settings-bound (buttons, labels — the learned-words page) bind no
/// setting to restore and are exempt by type, gated by
/// <see cref="EverySettingsRowSpecKind_IsEitherGuarded_OrExplicitlyExempt"/> so a future
/// settings-bound spec KIND cannot join the page silently exempt.</para>
///
/// <para>Duplicate settings-row Ids surface as a raw ArgumentException from the expected-map
/// ToDictionary — loud, it names the Id, and it is (checked) the suite's only row-Id-uniqueness
/// gate. Acceptable failure mode; noted here so it is not read as an accident.</para>
/// </summary>
public class KeyboardPageSourceResetDriftTests
{
    [Fact]
    public void ResetKeyboard_OnConfirmedModal_RestoresEverySettingsRowOnThePage()
    {
        var expected = DefaultViewByRowId();

        // Perturb every settings row through its OWN setter, then run the reset via the
        // confirmed modal. A row whose setting the handler forgot stays perturbed and is
        // named in the failure — against the pre-fix handler that row was k_sound.
        var ctx = TestSettingsPageContext.Build(showModal: (_, act) => act());
        var rows = new KeyboardPageSource(ctx).Build().SelectMany(p => p.Rows).ToList();
        foreach (var row in rows.Where(IsSettingsRow))
        {
            Perturb(row);
            // A row whose Set is a no-op reads its default before AND after the reset, so the
            // guard would pass vacuously for it (review round 1, finding 1: a dead setter
            // survived 9/9 green). Require the perturbation to have actually landed before the
            // reset is allowed to look correct.
            Check.WithCustomMessage(
                $"Perturbing '{row.Id}' did not change its value — the row's own Set is a no-op, "
                + "so this row's reset is not actually under test.")
                .That(Read(row)).Not.IsEqualTo(expected[row.Id]);
        }

        rows.OfType<ButtonSpec>().Single(b => b.Id == "k_reset").OnActivate();

        // Read back through a fresh Build — the same path RebuildContent takes in the window.
        var drifted = new KeyboardPageSource(ctx).Build().SelectMany(p => p.Rows)
            .Where(IsSettingsRow)
            .Select(r => (Id: r.Id, Got: Read(r)))
            .Where(x => !expected.TryGetValue(x.Id, out var def) || !Equals(x.Got, def))
            .Select(x => expected.TryGetValue(x.Id, out var def)
                ? $"{x.Id}: still {x.Got}, default {def}"
                : $"{x.Id}: present after reset but absent from a fresh default page build")
            .ToList();

        Check.WithCustomMessage(
            "k_reset must restore every settings row on the Keyboard page to its default; " +
            "these rows kept their perturbed value (add the setting to the reset handler): "
            + string.Join("; ", drifted))
            .That(drifted).IsEmpty();
    }

    [Fact]
    public void EverySettingsRowSpecKind_IsEitherGuarded_OrExplicitlyExempt()
    {
        // The guard can only perturb kinds it knows. A new settings-bound SettingsRowSpec
        // subtype would otherwise join the page silently exempt — defended until now only by
        // the unreachable default-arms in Read/Perturb (review round 1, finding 5). Reflection
        // over the shipping assembly, mirroring the completeness gate's page-source check
        // (SettingsRowDescriptionCompletenessTests).
        var unaccounted = typeof(SettingsRowSpec).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(SettingsRowSpec).IsAssignableFrom(t))
            .Where(t => !SettingsKinds.Any(k => k.IsAssignableFrom(t)))
            .Where(t => !NotASettingsKind.ContainsKey(t.Name))
            .Select(t => t.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
            "These SettingsRowSpec subtypes are neither perturbable settings kinds nor named "
            + "exemptions: " + string.Join(", ", unaccounted)
            + ". A settings-bound kind must join SettingsKinds (with Read/Perturb arms) or the "
            + "guard leaves its rows silently uncovered; anything else goes to NotASettingsKind "
            + "with the reason it binds no setting.")
            .That(unaccounted).IsEmpty();

        // An exemption naming a kind that no longer exists is stale bookkeeping that would
        // quietly excuse a future spec reusing the name.
        var declared = typeof(SettingsRowSpec).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(SettingsRowSpec).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        var phantom = NotASettingsKind.Keys
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
            "These exemptions name spec kinds that no longer exist: " + string.Join(", ", phantom))
            .That(phantom).IsEmpty();
    }

    /// <summary>
    /// The settings-bound spec kinds the guard can read and perturb. Membership is
    /// assignability (subclass-tolerant, same semantics as the old `is` filter), so a subtype
    /// of a settings kind is covered automatically; anything ELSE must appear in
    /// <see cref="NotASettingsKind"/> or the reflection gate above fails.
    /// </summary>
    private static readonly HashSet<Type> SettingsKinds =
    [
        typeof(SliderSpec), typeof(ToggleSpec), typeof(ComboBoxSpec), typeof(ColorPickerSpec)
    ];

    /// <summary>Spec kinds deliberately outside <see cref="SettingsKinds"/>, each with the reason.</summary>
    private static readonly Dictionary<string, string> NotASettingsKind = new(StringComparer.Ordinal)
    {
        [nameof(ButtonSpec)] = "action row — binds no setting to restore",
        [nameof(LabelSpec)] = "static text — binds no setting",
    };

    private static bool IsSettingsRow(SettingsRowSpec r) =>
        SettingsKinds.Any(k => k.IsInstanceOfType(r));

    /// <summary>The page's getter view over a fresh default settings object: row Id → value.</summary>
    private static Dictionary<string, object> DefaultViewByRowId() =>
        new KeyboardPageSource(TestSettingsPageContext.Build())
            .Build().SelectMany(p => p.Rows).Where(IsSettingsRow)
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
                c.Set(options.First(o => !string.Equals(o, c.Get(), StringComparison.Ordinal)));
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
