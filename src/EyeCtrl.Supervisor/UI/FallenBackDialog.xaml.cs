namespace EyeCtrl.Supervisor.UI;

using System;
using System.Windows;
using EyeCtrl.Controls;
using EyeCtrl.Core.Processing;
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

    public FallenBackDialog()
    {
        InitializeComponent();
        BuildButtons();
    }

    private void BuildButtons()
    {
        // Large hit areas + large font: this is shown when the app is broken; the user may
        // be driving it through a fallback AT and needs forgivingly-sized targets.
        var retry = new DwellButton(
            registrar: _registrar,
            id: "fb_retry",
            label: "Retry EyeCtrl (once)",
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
            label: "Dismiss (keep fallback)",
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

    private void OnRetry()
    {
        RetryRequested?.Invoke();
        // Task 20 owns the close-on-retry decision; keep the dialog up until then so the
        // user sees confirmation their click registered.
    }

    private void OnDismiss()
    {
        Dismissed?.Invoke();
        Close();
    }
}
