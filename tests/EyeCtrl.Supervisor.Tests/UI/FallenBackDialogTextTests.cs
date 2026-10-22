namespace EyeCtrl.Supervisor.Tests.UI;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Core.Localization;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.UI;
using NFluent;
using Xunit;

/// <summary>
/// T06 fixup-01 F1: the FallenBack dialog's moved English needs literal pins at its site — key
/// coverage cannot see a key used in the wrong slot. English literals on the expected side by
/// design. The dialog is constructed headless (never <see cref="System.Windows.Window.Show"/>ed)
/// on an STA thread, like the setup window's harness.
/// </summary>
[Collection(SupervisorXamlCollection.Name)]
public class FallenBackDialogTextTests
{
    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void TheDialog_ShowsItsEnglishTexts_WithRetryLeading()
    {
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
            try
            {
                Check.That(dialog.HeadlineText.Text).IsEqualTo("EyeCtrl couldn't stay running");
                Check.That(dialog.BodyText.Text).IsEqualTo(
                    "A fallback eye-control app has been launched. Use it (or a mouse) to choose an option below.");

                var buttons = dialog.ButtonHost.Children.OfType<Border>().ToList();
                Check.That(buttons).HasSize(2);

                // Retry is the green one and leads; a swap of the two labels or colours fails here.
                Check.That(TextsUnder(buttons[0])).Contains("Retry EyeCtrl (once)");
                Check.That(((SolidColorBrush)buttons[0].Background).Color)
                    .IsEqualTo(Color.FromRgb(30, 100, 30));

                Check.That(TextsUnder(buttons[1])).Contains("Dismiss (keep fallback)");
                Check.That(((SolidColorBrush)buttons[1].Background).Color)
                    .IsEqualTo(Color.FromRgb(80, 40, 40));
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>A3: the auto-retry countdown line appears only when the session's one countdown is
    /// armed, and its text names the countdown the <see cref="FallenBackAutoRetry"/> enforces.</summary>
    [Fact]
    public void TheDialog_ShowsTheAutoRetryCountdown_WhenTheCountdownIsArmed()
    {
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(StringTable.EmbeddedEnglish);
            try
            {
                // Unarmed (a second FallenBack, or a countdown already fired or cancelled): no line.
                Check.That(dialog.CountdownText.Visibility).IsEqualTo(Visibility.Collapsed);

                dialog.ShowAutoRetryCountdown();
                Check.That(dialog.CountdownText.Visibility).IsEqualTo(Visibility.Visible);
                Check.That(dialog.CountdownText.Text).IsEqualTo("Trying again in 60 s…");
                Check.That(dialog.CountdownText.Text).Contains(
                    ((int)FallenBackAutoRetry.Delay.TotalSeconds).ToString(
                        System.Globalization.CultureInfo.InvariantCulture));

                // The line goes away when the countdown does: a dialog left open after a failed
                // launch must not claim a retry is still coming.
                dialog.ClearAutoRetryCountdown();
                Check.That(dialog.CountdownText.Visibility).IsEqualTo(Visibility.Collapsed);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>Every non-empty TextBlock text under <paramref name="node"/> (a DwellButton's
    /// caption is inside its root border).</summary>
    private static List<string> TextsUnder(DependencyObject node)
    {
        var found = new List<string>();

        void Walk(DependencyObject current)
        {
            if (current is TextBlock text && !string.IsNullOrEmpty(text.Text))
                found.Add(text.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                Walk(child);
        }

        Walk(node);
        return found;
    }
}
