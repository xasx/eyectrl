namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The mode toggle's resting-gaze behaviour with a real <see cref="SettingsWindowViewModel"/>
/// and real <see cref="SettingsPagerRearmGuard"/>: a gaze resting on the toggle flips it exactly
/// once until it leaves the RELEASE circle (85 DIP), not the hit circle (50 DIP) — the 50/85
/// hysteresis of owner ruling L16. Phase (b) is the assertion that proves hysteresis rather than
/// a plain re-arm-on-exit: a wander at 70 logical DIP sits outside the hit circle but inside the
/// release circle, and the guard must hold through it.
///
/// <para>
/// Own file, not a member of <see cref="SettingsModeToggleWiringTests"/>:
/// <c>DpiHelperWriterIsolationTests</c>' gate matches per FILE, and this file calls
/// <c>DpiHelper.Initialize</c> — so it joins "DpiHelper Tests (App)"
/// (ManualSelectionDwellServiceTests.cs:57 is the precedent collection) while the source-scan
/// pins stay in the parallel phase. The scale is 2.0 (owner-realism choice): every offset
/// derives through <c>DpiHelper.LogicalToPhysicalX</c>, so the test is scale-correct at any
/// scale, and running it off 1.0 is what catches a physical/logical mix-up.
/// </para>
///
/// <para>
/// The guard/VM interaction mirrors the view's wiring pinned in
/// <see cref="SettingsModeToggleWiringTests"/>: the registration consults
/// <c>IsDisarmed</c> (the guard IS unregistration), the activation disarms at the release
/// radius around the tracked centre, and the guard's Update runs BEFORE each frame's
/// GazeCursorMovedMessage — production's order (SettingsPagerRearmGuard feeds from
/// UpdateGaze ahead of the position pass). Nothing is shown; no UI category, so CI runs it.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class SettingsModeToggleRestingGazeTests
{
    [StaFact]
    public void RestingGazeOnTheModeToggle_FlipsExactlyOnce_UntilTheGazeLeaves()
    {
        DpiHelper.Initialize(2.0, 2.0);              // 2.0 is the owner-realism choice: every
        try                                            // offset derives through the same scale, so
                                                       // the test is scale-correct at any scale
        {
            var vm = QaTestSupport.MakeViewModel();  // per the pager precedent's construction
            var t = 0L;                               // running timestamp, the precedent's
                                                       // discipline (SettingsPagerChromeTests.cs:744-766)
            void Frame(PhysicalPoint g) { vm.Receive(new GazeCursorMovedMessage(g, t)); t += 33_000; }
            SettingsPagerRearmGuard? guard = null;
            var flips = 0;
            var centre = new PhysicalPoint(DpiHelper.LogicalToPhysicalX(1000), DpiHelper.LogicalToPhysicalX(300));
            var hit = DpiHelper.LogicalToPhysicalX(SettingsWindow.ModeToggleHitRadius);      // 50 logical
            var release = DpiHelper.LogicalToPhysicalX(SettingsWindow.ModeToggleReleaseRadius); // 85 logical
            guard = new SettingsPagerRearmGuard(vm, (_, _) => Register());

            void Register()
            {
                if (guard!.IsDisarmed("settings_mode")) return;   // mirrors RegisterModeToggleTarget
                vm.Register("settings_mode", DwellCategory.Pagination, hit, 400,
                    () => { flips++; guard.Disarm("settings_mode", DwellCategory.Pagination, centre, release); });
                vm.UpdatePosition("settings_mode", DwellCategory.Pagination, centre);
            }
            Register();

            // (a) 200 frames resting ON the centre: the cooldown re-arms the detector; the guard
            // must hold — exactly one flip. guard.Update runs BEFORE the frame (production's
            // order, SettingsPagerRearmGuard.cs:82-84 — round-3 m-1).
            for (var i = 0; i < 200; i++) { guard.Update(centre); Frame(centre); }
            Check.That(flips).IsEqualTo(1);

            // (b) wander at 70 LOGICAL DIP — outside the 50 hit circle, inside the 85 release
            // circle: the hysteresis must hold the guard (the assertion that proves 50/85, not 50/50).
            var wander = new PhysicalPoint(centre.X + DpiHelper.LogicalToPhysicalX(70), centre.Y);
            for (var i = 0; i < 50; i++) guard.Update(wander);
            for (var i = 0; i < 20; i++) { guard.Update(centre); Frame(centre); }
            Check.That(flips).IsEqualTo(1);

            // (c) leave beyond the release radius, return: exactly one more flip.
            var away = new PhysicalPoint(centre.X + DpiHelper.LogicalToPhysicalX(120), centre.Y);
            guard.Update(away);                       // re-arm -> Register() via the callback seam
            Frame(away);                              // the away frame reaches the VM too
            for (var i = 0; i < 20; i++) { guard.Update(centre); Frame(centre); }
            Check.That(flips).IsEqualTo(2);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);           // restore the ambient scale
        }
    }
}
