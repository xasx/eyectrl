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
    /// <summary>Master switch. When false, adaptation is fully inert (effective settings == baseline).</summary>
    [ObservableProperty] private bool _enabled;

    /// <summary>How eagerly adaptation responds. Conservative by default.</summary>
    [ObservableProperty] private AdaptationPosture _posture = AdaptationPosture.Conservative;

    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => Array.Empty<string>();
}
