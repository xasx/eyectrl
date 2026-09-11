namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Diagnostic instrumentation that is off in normal use. Everything here is opt-in: the shipped
/// default must leave the gaze path exactly as it was before the instrument existed.
/// </summary>
public partial class DiagnosticsSettings : ObservableObject, IValidatable
{
    /// <summary>
    /// Whether raw gaze samples are written to a local recording file for offline analysis.
    /// <para>
    /// Default <c>false</c>, and deliberately so: a recording is a privacy surface (it is a trace of
    /// where its owner looked), and the recorder costs per-sample work on the gaze thread. Only the
    /// user turns this on, only for a measurement session, and the recording never leaves the machine —
    /// no window titles, no target ids, nothing but gaze coordinates and timing.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _recordRawGaze;

    /// <summary>
    /// Whether the two UI-lane instruments run: dispatch enqueue→execute latency, and cursor-ring
    /// position quantization plus cadence.
    /// <para>
    /// Default <c>false</c>. Separate from <see cref="RecordRawGaze"/> deliberately — these measure the
    /// UI thread and record no gaze coordinates at all, so an operator answering the dispatch-latency
    /// question should not have to also produce a trace of where they looked. One flag covers both
    /// instruments because they answer the same session's questions and are run together.
    /// </para>
    /// <para>
    /// Neither instrument changes any behaviour: no dispatch is coalesced, delayed or reordered, and
    /// the ring is positioned exactly as before. They only observe.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool _measureUiInstruments;

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately empty. Every setting here is a bool, which has no invalid value, and a range check
    /// on anything added later would be a liability rather than a safeguard: <c>SettingsService.Load</c>
    /// answers ANY validation error by discarding the whole file and returning all-defaults, so a new
    /// range on a diagnostic setting can cost the user every other setting they have configured.
    /// </remarks>
    public IReadOnlyList<string> Validate() => Array.Empty<string>();
}
