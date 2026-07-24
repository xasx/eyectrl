namespace EyeCtrl.Core.Settings;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Master switch + posture for struggle-detection self-adaptation. Off by default (opt-in v1).
/// Per-knob safety bands and feedback toggles are added in Phase 1b, when a controller consumes them.
/// </summary>
public partial class AdaptationSettings : ObservableObject, IValidatable
{
    /// <summary>Master switch. When false, the adaptation <em>controller</em> is fully inert: it
    /// produces zero effective-value deltas, so effective settings equal the user's baseline. This
    /// gates adaptation <em>effects</em> only — telemetry (dwell outcomes + gaze-quality summaries)
    /// is always collected into the bounded buffers so data is available the moment adaptation is
    /// enabled; nothing consumes those buffers until the Phase-1b StruggleMonitor lands.</summary>
    [ObservableProperty] private bool _enabled;

    /// <summary>How eagerly adaptation responds. Conservative by default.</summary>
    [ObservableProperty] private AdaptationPosture _posture = AdaptationPosture.Conservative;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => Array.Empty<string>();
}
