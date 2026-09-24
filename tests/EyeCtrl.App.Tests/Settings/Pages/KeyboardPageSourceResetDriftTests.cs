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
/// ToDictionary — loud, it names the Id, and it is the only such gate covering the Keyboard
/// page: sibling page-source tests key their own pages' rows by Id the same way, so the
/// failure mode is suite-wide, not unique here. Acceptable; noted so it is not read as an
/// accident.</para>
/// </summary>
public class KeyboardPageSourceResetDriftTests
{
    [Fact]
    public void ResetKeyboard_OnConfirmedModal_RestoresEverySettingsRowOnThePage()
    {
        // The machinery lives in SettingsPageResetDriftGuard (shared with the Dwell page, and
        // available to the eight deferred pages); this file supplies the Keyboard parameters.
        SettingsPageResetDriftGuard.ResetRestoresEverySettingsRow(
            ctx => new KeyboardPageSource(ctx).Build(),
            resetButtonId: "k_reset",
            pageName: "Keyboard");
    }

    [Fact]
    public void EverySettingsRowSpecKind_IsEitherGuarded_OrExplicitlyExempt()
    {
        // The guard can only perturb kinds it knows. A new settings-bound SettingsRowSpec
        // subtype would otherwise join the page silently exempt — defended until now only by
        // the unreachable default-arms in Read/Perturb (review round 1, finding 5). Reflection
        // over the shipping assembly, mirroring the completeness gate's page-source check
        // (SettingsRowDescriptionCompletenessTests).
        var specTypes = typeof(SettingsRowSpec).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(SettingsRowSpec).IsAssignableFrom(t))
            .ToList();

        // Exemptions are keyed by FullName, not Name: a type named LabelSpec in another
        // namespace would collide with the simple-name key and slip through unnamed
        // (review round 2, N6).
        var unaccounted = specTypes
            .Where(t => !SettingsPageResetDriftGuard.SettingsKinds.Any(k => k.IsAssignableFrom(t)))
            .Where(t => !SettingsPageResetDriftGuard.NotASettingsKind.ContainsKey(t.FullName!))
            .Select(t => t.FullName!)
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
        var phantom = SettingsPageResetDriftGuard.NotASettingsKind.Keys
            .Where(name => specTypes.All(t => t.FullName != name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Check.WithCustomMessage(
            "These exemptions name spec kinds that no longer exist: " + string.Join(", ", phantom))
            .That(phantom).IsEmpty();
    }
}
