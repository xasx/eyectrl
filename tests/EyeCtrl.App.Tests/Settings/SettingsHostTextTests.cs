namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// T07 fixup-02 R3: the host-supplied text <see cref="SettingsWindow"/> passes into the shared
/// controls. Unlike a page source, these strings are not row specs, so the row golden cannot see
/// them: a swap here mislabels EVERY Settings toggle or every swatch at once (review 02 S09, S10).
/// Built through the production construction path — <see cref="SettingsWindow.CreateControl"/> and
/// the real <see cref="DwellToggle"/> tree — so a change to the control's rendering moves this
/// test, not a probe.
///
/// <para>
/// Nothing is shown: a detached element tree renders no window. The toggle probe is STA (WPF
/// elements), the swatch probe is not.
/// </para>
/// </summary>
public class SettingsHostTextTests
{
    /// <summary>Physical hit radius, matching <c>SettingsContentFitTests.RadiusPhysical</c>.</summary>
    private const double RadiusPhysical = SettingsWindow.ControlBtnRadius;

    /// <summary>An arbitrary dwell, matching the fit suite's probe.</summary>
    private const int DwellMs = 800;

    /// <summary>Wide enough for the two-column row form, so the probe measures the normal layout.</summary>
    private const double WideColumnWidth = 1424.0;

    [Fact]
    public void SwatchKeys_ResolveToThePresetNamesInOrder()
    {
        // The picker pairs each swatch with DwellColorPicker.PresetsFor by position; a swapped
        // key paints one colour and names another. Count is pinned by ColorSwatchNamesTests; the
        // words and their ORDER are pinned here.
        Check.WithCustomMessage(
                "the swatch names no longer resolve, in order, to the presets' English names.")
            .That(SettingsWindow.ColorSwatchNames.Select(
                key => StringTable.EmbeddedEnglish.T(key)))
            .ContainsExactly("Accent", "Green", "Warning", "Text");
    }

    [StaFact]
    public void ARealToggleRowsStateText_SaysOffWhenOff_AndOnWhenOn()
    {
        var off = StateTextsOfToggle(() => false);
        var on = StateTextsOfToggle(() => true);

        Check.WithCustomMessage(
                $"an off toggle's row reads {string.Join(" | ", off)}; the state word must be "
                + "\"Off\". A swap (S09) makes every Settings toggle read \"On\" while it is off.")
            .That(off).Contains("Off");
        Check.WithCustomMessage(
                $"an on toggle's row reads {string.Join(" | ", on)}; the state word must be \"On\".")
            .That(on).Contains("On");
        Check.WithCustomMessage("an off toggle drew the on word as well.")
            .That(off).Not.Contains("On");
        Check.WithCustomMessage("an on toggle drew the off word as well.")
            .That(on).Not.Contains("Off");
    }

    /// <summary>
    /// Builds one toggle row through the production path and returns every TextBlock of its
    /// arranged tree, so the state word can be asserted as the user sees it.
    /// </summary>
    /// <param name="get">The toggle's live value.</param>
    /// <returns>The row's text blocks' texts.</returns>
    private static List<string> StateTextsOfToggle(Func<bool> get)
    {
        var composite = (IDwellComposite)SettingsWindow.CreateControl(
            new ToggleSpec("on_off_probe", "Probe", get, _ => { }),
            new SettingsAutoPagination.NoopDwellRegistrar(),
            RadiusPhysical, DwellMs, WideColumnWidth);

        var host = new Grid { Width = WideColumnWidth };
        TextElement.SetFontFamily(host, Tokens.FontStandard);
        composite.Attach(host);
        host.Measure(new Size(WideColumnWidth, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, WideColumnWidth, host.DesiredSize.Height));
        host.UpdateLayout();

        return FindTextBlocks(host)
            .Select(t => t.Text)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList();
    }

    private static IEnumerable<TextBlock> FindTextBlocks(Visual root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is not Visual child) continue;
            if (child is TextBlock tb) yield return tb;
            foreach (var nested in FindTextBlocks(child)) yield return nested;
        }
    }
}
