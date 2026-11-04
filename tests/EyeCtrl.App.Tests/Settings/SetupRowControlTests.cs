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
/// </summary>
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
        // The existing ~160 ButtonSpecs (Reset rows etc.) keep their exact shape and pitch.
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
}
