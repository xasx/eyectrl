namespace EyeCtrl.App.Tests.Settings;

using System.Windows.Controls;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;

/// <summary>
/// Review 02 R4 at the control layer: a described ButtonSpec renders its description beneath the
/// button (before this, the description was silently dropped — the „Erweitert" rows' "names both
/// versions" requirement depended on it), and the probe placeholder is a label whose MinHeight
/// holds roughly the same footprint. Headless: the controls are constructed and measured, never
/// shown.
///
/// <para>
/// The renderer change is deliberately NOT inert: three pre-existing rows carried descriptions that
/// were dropped until now and render them since — <c>ad_reset_learned</c> and <c>ad_log</c> on
/// „Anpassung", <c>dg_open_folder</c> on „Diagnose". That was accepted by the coordinator (review 03
/// S2) as wanted behaviour, and is pinned by <c>AnExistingDescribedRow_NowRendersItsDescription</c>.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class SetupRowControlTests
{
    private const double RadiusPhysical = 24;
    private const int DwellMs = 600;

    [StaFact]
    public void ADescribedButton_RendersItsDescriptionUnderTheButton()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        const string description = "Version 2.0.0 ist bereit (installiert: 1.0.0).";

        var control = SettingsWindow.CreateControl(
            new ButtonSpec("av_update", "Update installieren", () => { },
                ButtonKind.Default, Width: 380, Height: 100)
            {
                Description = description,
            },
            new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, availableWidth: 1000);

        var composite = control as IDwellComposite;
        Check.WithCustomMessage("A described ButtonSpec must render as a composite (button + description).")
            .That(composite).IsNotNull();
        Check.That(composite!.Children).HasSize(1);
        Check.That(composite.Children[0]).IsInstanceOf<DwellButton>();

        var host = new Grid();
        composite.Attach(host);
        var stack = host.Children[0] as StackPanel;
        Check.That(stack).IsNotNull();
        Check.That(stack!.Children).HasSize(2);
        var text = stack.Children[1] as TextBlock;
        Check.That(text).IsNotNull();
        Check.That(text!.Text).IsEqualTo(description);
    }

    [StaFact]
    public void AnUndescribedButton_StaysABareDwellButton()
    {
        // A ButtonSpec WITHOUT a description keeps its exact shape and pitch — the many Reset
        // rows among them. (Described specs are the three pre-existing rows above plus S6f's.)
        WpfApplicationGate.EnsureApplicationWithTokens();

        var control = SettingsWindow.CreateControl(
            new ButtonSpec("av_reset", "Reset Advanced Settings", () => { },
                ButtonKind.Reset, Width: 300, Height: 100),
            new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, availableWidth: 1000);

        Check.That(control).IsInstanceOf<DwellButton>();
    }

    [StaFact]
    public void TheProbePlaceholder_IsANonDwellableLabelAtTheDescribedRowHeight()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var control = SettingsWindow.CreateControl(
            new LabelSpec(() => "Update wird geprüft", Wrap: false,
                MinHeight: SettingsRowSpec.DescribedButtonRowHeight),
            new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, availableWidth: 1000);

        var label = control as DwellLabel;
        Check.That(label).IsNotNull();
        Check.That(label!.Root.MinHeight).IsEqualTo(SettingsRowSpec.DescribedButtonRowHeight);
        Check.That(label.Root.Text).IsEqualTo("Update wird geprüft");
    }

    /// <summary>
    /// Review 03 S2: three pre-existing ButtonSpecs carried descriptions that were dropped until
    /// the described-row renderer; the coordinator accepted showing them. Pinned on the Diagnostics
    /// row that sits directly above C10's sub-pixel probe button.
    /// </summary>
    [StaFact]
    public void AnExistingDescribedRow_NowRendersItsDescription()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var context = EyeCtrl.App.Tests.Settings.Pages.TestSettingsPageContext.Build();
        var spec = new DiagnosticsPageSource(context).Build()
            .SelectMany(page => page.Rows)
            .OfType<ButtonSpec>()
            .Single(row => row.Id == "dg_open_folder");

        Check.WithCustomMessage("dg_open_folder carries a description in its spec.")
            .That(string.IsNullOrWhiteSpace(spec.Description)).IsFalse();

        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            spec, new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, availableWidth: 1000);
        var host = new Grid();
        composite.Attach(host);
        var stack = host.Children[0] as StackPanel;
        Check.That(stack).IsNotNull();
        var text = stack!.Children[1] as TextBlock;
        Check.That(text).IsNotNull();
        Check.WithCustomMessage("dg_open_folder's description must render beneath its button.")
            .That(text!.Text).IsEqualTo(spec.Description);
    }

    /// <summary>
    /// The gaze-rate contract for the new composite (review 02 R4): the frame loop reads
    /// <c>Children</c> once per composite per frame, so the list must be a cached field, not built
    /// per get. Named by the census in EyeCtrl.Controls.Tests
    /// (<c>TheAppTestsCaseCoveringDescribedButtonRow_StillExists</c>).
    /// </summary>
    [StaFact]
    public void ADescribedButton_ForwardsItsCachedChildren()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            new ButtonSpec("av_update", "Update installieren", () => { },
                ButtonKind.Default, Width: 380, Height: 100)
            {
                Description = "Version 2.0.0 ist bereit (installiert: 1.0.0).",
            },
            new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, availableWidth: 1000);

        var first = composite.Children;
        var second = composite.Children;

        Check.WithCustomMessage(
                "A described button row handed out a different Children object on the second get, "
                + "so it allocates once per row per gaze frame in UpdateAllTargetPositions. Cache "
                + "the list; never build one per get.")
            .That(ReferenceEquals(first, second)).IsTrue();
        Check.That(first).HasSize(1);
    }
}
