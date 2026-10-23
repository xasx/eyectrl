namespace EyeCtrl.Supervisor.UI;

using System;
using System.Windows;
using EyeCtrl.Controls;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Processing;
using EyeCtrl.Supervisor.Supervision;
using Media = System.Windows.Media;

/// <summary>
/// Non-modal, topmost crash-recovery dialog shown when the supervisor enters the
/// <c>FallenBack</c> state (EyeCtrl is in a crash loop and a fallback AT has been launched).
/// </summary>
/// <remarks>
/// The dialog is the safety net: the user's usual dwell surface (the EyeCtrl app) is the
/// thing that's broken, so every interactive control is a <see cref="DwellButton"/> —
/// dwell-operable in principle, and click-operable in practice (the fallback AT already
/// running drives the OS cursor; <see cref="DwellButton"/> has a mouse-click fallback).
/// Non-modal + topmost so the user can act on it through whichever AT is providing cursor
/// control, without the dialog blocking input or hiding behind the fallback app.
/// </remarks>
public partial class FallenBackDialog : Window
{
    private readonly NoopDwellRegistrar _registrar = new();

    /// <summary>Raised when the user picks "Retry". Task 20 relaunches the app with <c>--paused</c> (paused-retry handoff).</summary>
    public event Action? RetryRequested;

    /// <summary>Raised when the user picks "Dismiss". The dialog closes and supervision stays paused for the session.</summary>
    public event Action? Dismissed;

    /// <summary>The table this dialog was opened with.</summary>
    internal StringTable Strings { get; }

    /// <param name="strings">The Supervisor's table, resolved when the dialog opens — the pending
    /// language choice first, then the setting (T05b) — so a dialog opened after a language change
    /// shows the new language.</param>
    public FallenBackDialog(StringTable strings)
    {
        Strings = strings;
        InitializeComponent();
        HeadlineText.Text = strings.T("supervisor.fallback.title");
        BodyText.Text = strings.T("supervisor.fallback.body");
        BuildButtons();
    }

    /// <summary>Shows the auto-retry countdown line ("Trying again in 60 s…") — A3's safety net for
    /// a user who cannot click Retry. Called when the session's one countdown is armed for this
    /// episode; the text comes from the dialog's own table, with the countdown's SECONDS as the
    /// format argument (fixup-02 addendum: the number is never baked into the string).</summary>
    internal void ShowAutoRetryCountdown()
    {
        CountdownText.Text = Strings.T(
            "supervisor.fallback.countdown", (int)FallenBackAutoRetry.Delay.TotalSeconds);
        CountdownText.Visibility = Visibility.Visible;
    }

    /// <summary>Hides the countdown line once the countdown has fired or been cancelled, so a
    /// dialog left open (a failed launch) never claims a retry is still pending.</summary>
    internal void ClearAutoRetryCountdown()
    {
        CountdownText.Visibility = Visibility.Collapsed;
    }

    private void BuildButtons()
    {
        // Large hit areas + large font: this is shown when the app is broken; the user may
        // be driving it through a fallback AT and needs forgivingly-sized targets.
        var retry = new DwellButton(
            registrar: _registrar,
            id: "fb_retry",
            label: Strings.T("supervisor.fallback.retry"),
            action: OnRetry,
            baseColor: Media.Color.FromRgb(30, 100, 30),
            category: DwellCategory.Modal,
            radiusPhysical: 200,
            dwellMs: 600,
            width: 260,
            height: 110);

        var dismiss = new DwellButton(
            registrar: _registrar,
            id: "fb_dismiss",
            label: Strings.T("supervisor.fallback.dismiss"),
            action: OnDismiss,
            baseColor: Media.Color.FromRgb(80, 40, 40),
            category: DwellCategory.Modal,
            radiusPhysical: 200,
            dwellMs: 600,
            width: 260,
            height: 110);

        // DwellButton.Attach both adds Root to the panel and registers for dwell detection.
        retry.Attach(ButtonHost);
        dismiss.Attach(ButtonHost);
    }

    /// <summary>The Retry button's action - internal so the wiring tests can drive the real button
    /// path without showing the dialog.</summary>
    internal void OnRetry()
    {
        RetryRequested?.Invoke();
        // Task 20 owns the close-on-retry decision; keep the dialog up until then so the
        // user sees confirmation their click registered.
    }

    /// <summary>The Dismiss button's action - internal so the wiring tests can drive the real
    /// button path without showing the dialog.</summary>
    internal void OnDismiss()
    {
        Dismissed?.Invoke();
        Close();
    }
}
