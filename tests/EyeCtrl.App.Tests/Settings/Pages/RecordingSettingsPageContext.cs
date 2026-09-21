namespace EyeCtrl.App.Tests.Settings.Pages;

using System.Collections.Generic;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

/// <summary>
/// A <see cref="SettingsPageContext"/> whose view-bound delegates record what the page sources do,
/// so the load-bearing callbacks this refactor transcribed (reset field-copy lists, cal_run
/// close-then-send ordering, model deletion, QA transitions) can be exercised headlessly. The
/// plain <see cref="TestSettingsPageContext"/> hard-wires those delegates to no-ops, which is fine
/// for structural assertions but cannot verify behaviour.
///
/// <para><b>ShowModal captures without invoking.</b> The Yes callback is held in
/// <see cref="LastModalOnYes"/>; a test simulates the user confirming by invoking it explicitly,
/// so the pre-confirm and post-confirm states are both assertable.</para>
///
/// <para><b>The messenger records into the same ordered log as the delegates.</b> That is what
/// makes <c>cal_run</c>'s Close-then-Send ordering verifiable: both append to <see cref="Log"/> in
/// call order. Uses a real <see cref="WeakReferenceMessenger"/> (the production type) with
/// registered recipients rather than a hand-rolled <see cref="IMessenger"/> spy — fewer moving
/// parts, and it exercises the same delivery path the shell does.</para>
/// </summary>
internal sealed class RecordingSettingsPageContext
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly List<string> _log = new();
    private readonly List<string> _errors = new();

    /// <param name="clipboard">
    /// What <see cref="SettingsPageContext.ReadClipboard"/> returns. Configurable because
    /// <c>ModelsPageSource.DownloadCustomFromClipboard</c> reads it first and rejects anything that
    /// is not an HTTPS URL — a fixed <c>""</c> makes every branch past that guard unreachable.
    /// </param>
    public RecordingSettingsPageContext(
        EyeCtrlSettings? settings = null,
        SettingsWindowViewModel? viewModel = null,
        ModelRegistryService? registry = null,
        ModelManagementService? modelManagement = null,
        CalibrationService? calibration = null,
        string clipboard = "")
    {
        // Recipients are kept alive by `this` for the scope of the test (WeakReferenceMessenger
        // holds them weakly). Only the message types a page source actually Sends are registered —
        // registering every message type would be dead weight and would couple this helper to the
        // whole message catalogue.
        _messenger.Register<RecordingSettingsPageContext, CalibrationRequestedMessage>(
            this, (_, _) => _log.Add(nameof(CalibrationRequestedMessage)));
        _messenger.Register<RecordingSettingsPageContext, CalibrationClearRequestedMessage>(
            this, (_, _) => _log.Add(nameof(CalibrationClearRequestedMessage)));

        Context = new SettingsPageContext(
            settings ?? new EyeCtrlSettings(),
            viewModel!, registry!, modelManagement!,
            calibration ?? new CalibrationService(),
            _messenger,
            restart: null!,
            dictionary: null,
            showModal: CaptureModal,
            showError: message => { _errors.Add(message); _log.Add(ErrorEntry); },
            applyAndSave: () => { ApplyAndSaveCount++; _log.Add(ApplyAndSaveEntry); },
            rebuildContent: () => { RebuildContentCount++; _log.Add(RebuildContentEntry); },
            invoke: action => action(),   // single-threaded tests: marshal is a direct call
            close: () => { CloseCount++; _log.Add(CloseEntry); },
            readClipboard: () => clipboard);
    }

    public const string ApplyAndSaveEntry = "ApplyAndSave";
    public const string RebuildContentEntry = "RebuildContent";
    public const string CloseEntry = "Close";
    public const string ErrorEntry = "ShowError";

    public SettingsPageContext Context { get; }

    public int ApplyAndSaveCount { get; private set; }
    public int RebuildContentCount { get; private set; }
    public int CloseCount { get; private set; }

    /// <summary>The most recent modal message, or null if <see cref="SettingsPageContext.ShowModal"/>
    /// was never called.</summary>
    public string? LastModalMessage { get; private set; }

    /// <summary>The most recent modal Yes callback, captured but NOT invoked. Null if no modal was
    /// shown. A test simulates the user confirming by invoking this.</summary>
    public Action? LastModalOnYes { get; private set; }

    /// <summary>Ordered event log shared by the view-bound delegates and the messenger, so call
    /// order across the two surfaces (e.g. Close before Send) is assertable.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>
    /// Messages passed to <see cref="SettingsPageContext.ShowError"/>, in order. Recorded rather
    /// than discarded: several page-source branches exist only to report a failure to the user, so
    /// a no-op <c>showError</c> leaves them indistinguishable from doing nothing at all.
    /// </summary>
    public IReadOnlyList<string> Errors => _errors;

    private void CaptureModal(string message, Action onYes)
    {
        LastModalMessage = message;
        LastModalOnYes = onYes;
        // Intentionally does NOT invoke onYes: the test decides whether the user confirms.
    }
}
