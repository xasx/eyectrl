namespace EyeCtrl.App.Startup;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;

/// <summary>The ring-arm look build's three arms (spec: ring-stutter-investigation/replay-2).</summary>
internal enum RingArm
{
    /// <summary>A: the original offer-following pin, no hold — a diagnostic arm since the owner's
    /// look made the hybrid the default.</summary>
    Today,

    /// <summary>B: the run-only pin with the hybrid freeze hold (500/90/150) — the default since
    /// the owner's look picked it (2026-10-16).</summary>
    Hybrid,

    /// <summary>C: the run-only pin, no hold — the dance reference, a diagnostic arm.</summary>
    RunOnly,
}

/// <summary>
/// The resolved <c>--ring-arm=today|hybrid|run-only</c> launch argument: the arm, whether the flag
/// was given at all (the event log exists only then), and the raw value when it was unknown. It is
/// NOT a setting — nothing persists, and the supervisor's own relaunch (which passes only
/// <c>--paused</c>) runs the default, hybrid.
/// </summary>
internal readonly record struct RingArmLaunch(RingArm Arm, bool Specified, string? UnknownValue)
{
    /// <summary>The Core half of the arm mapping: A = OfferFollowing; B and C = RunOnly.</summary>
    internal RingPinPolicy PinPolicy =>
        Arm == RingArm.Today ? RingPinPolicy.OfferFollowing : RingPinPolicy.RunOnly;

    /// <summary>The VM half of the arm mapping: only B = HybridFreeze.</summary>
    internal RingHoldPolicy HoldPolicy =>
        Arm == RingArm.Hybrid ? RingHoldPolicy.HybridFreeze : RingHoldPolicy.None;
}

/// <summary>
/// Parses <c>--ring-arm=…</c> in App.OnStartup, beside <c>--paused</c>. The DEFAULT is the hybrid
/// (the owner's pick from the look): absent means hybrid, and an unknown value also runs hybrid,
/// with the raw value carried so the App can warn while the startup line still shows
/// <c>[source: --ring-arm]</c>. The flag and value match case-insensitively, like the other launch
/// flags. The arm mapping lives here; the class defaults (<see cref="RingPinPolicy.OfferFollowing"/>,
/// <see cref="RingHoldPolicy.None"/>) are deliberately unchanged — this App launch is the only
/// composition root that selects an arm.
/// </summary>
internal static class RingArmArgument
{
    /// <summary>The flag's spelling on the command line, with its <c>=</c> separator.</summary>
    internal const string Flag = "--ring-arm=";

    /// <summary>Finds the first <paramref name="Flag"/> and resolves its value.</summary>
    internal static RingArmLaunch Parse(IReadOnlyList<string> args)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (!args[index].StartsWith(Flag, StringComparison.OrdinalIgnoreCase)) continue;

            var value = args[index][Flag.Length..];
            if (value.Equals("today", StringComparison.OrdinalIgnoreCase))
                return new RingArmLaunch(RingArm.Today, Specified: true, UnknownValue: null);
            if (value.Equals("hybrid", StringComparison.OrdinalIgnoreCase))
                return new RingArmLaunch(RingArm.Hybrid, Specified: true, UnknownValue: null);
            if (value.Equals("run-only", StringComparison.OrdinalIgnoreCase))
                return new RingArmLaunch(RingArm.RunOnly, Specified: true, UnknownValue: null);

            return new RingArmLaunch(RingArm.Hybrid, Specified: true, UnknownValue: value);
        }

        return new RingArmLaunch(RingArm.Hybrid, Specified: false, UnknownValue: null);
    }
}
