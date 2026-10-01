namespace EyeCtrl.App.Tests.Settings;

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Headless pins for the settings pagers' behaviour. The settings window cannot be constructed
/// here (eight non-optional dependencies), so the two pure pieces the view delegates to —
/// <see cref="SettingsPagerChrome"/> (boundary mapping, glyphs, dwell, actions) and
/// <see cref="SettingsPagerRearmGuard"/> (the wrap-jump re-arm rider) — are exercised directly,
/// with a real <see cref="SettingsWindowViewModel"/> for the activation semantics, and the view's
/// own wiring is held by source scans in <see cref="SettingsPagerWiringTests"/>.
///
/// Nothing is shown and no input is simulated: a detached element tree renders no window. No UI
/// category, so CI runs it.
/// </summary>
public class SettingsPagerChromeTests
{
    // ────── Boundary mapping ──────

    [Fact]
    public void IsBoundary_PrevAtTheFirstPage_NextAtTheLast()
    {
        Check.That(SettingsPagerChrome.IsBoundary(pageIndex: 0, pageCount: 4, direction: -1)).IsTrue();
        Check.That(SettingsPagerChrome.IsBoundary(pageIndex: 3, pageCount: 4, direction: +1)).IsTrue();

        Check.That(SettingsPagerChrome.IsBoundary(pageIndex: 0, pageCount: 4, direction: +1)).IsFalse();
        Check.That(SettingsPagerChrome.IsBoundary(pageIndex: 3, pageCount: 4, direction: -1)).IsFalse();
        Check.That(SettingsPagerChrome.IsBoundary(pageIndex: 1, pageCount: 4, direction: -1)).IsFalse();
        Check.That(SettingsPagerChrome.IsBoundary(pageIndex: 2, pageCount: 4, direction: +1)).IsFalse();
    }

    [Fact]
    public void TargetPage_JumpsToTheFarEnd_AtABoundary()
    {
        // At page 0 the previous button wraps to the LAST page; at the last page the next button
        // wraps to the FIRST.
        Check.That(SettingsPagerChrome.TargetPage(pageIndex: 0, pageCount: 4, direction: -1)).IsEqualTo(3);
        Check.That(SettingsPagerChrome.TargetPage(pageIndex: 3, pageCount: 4, direction: +1)).IsEqualTo(0);
    }

    [Fact]
    public void TargetPage_StepsOnePage_InsideTheRange()
    {
        Check.That(SettingsPagerChrome.TargetPage(pageIndex: 1, pageCount: 4, direction: -1)).IsEqualTo(0);
        Check.That(SettingsPagerChrome.TargetPage(pageIndex: 1, pageCount: 4, direction: +1)).IsEqualTo(2);
        Check.That(SettingsPagerChrome.TargetPage(pageIndex: 2, pageCount: 4, direction: -1)).IsEqualTo(1);
    }

    // ────── Glyphs ──────

    [Fact]
    public void GlyphFor_BoundaryButtonsWearTheDoubleArrows()
    {
        // ⏭ on the previous button (it jumps to the END), ⏮ on the next button (it jumps to the
        // START) — the same pair for both pagers.
        Check.That(SettingsPagerChrome.GlyphFor("pag_prev", boundary: true)).IsEqualTo("⏭");
        Check.That(SettingsPagerChrome.GlyphFor("pag_next", boundary: true)).IsEqualTo("⏮");
        Check.That(SettingsPagerChrome.GlyphFor("tab_prev", boundary: true)).IsEqualTo("⏭");
        Check.That(SettingsPagerChrome.GlyphFor("tab_next", boundary: true)).IsEqualTo("⏮");
    }

    [Fact]
    public void GlyphFor_StepButtonsKeepTheSingleArrows()
    {
        Check.That(SettingsPagerChrome.GlyphFor("pag_prev", boundary: false)).IsEqualTo("◀");
        Check.That(SettingsPagerChrome.GlyphFor("pag_next", boundary: false)).IsEqualTo("▶");
        Check.That(SettingsPagerChrome.GlyphFor("tab_prev", boundary: false)).IsEqualTo("▲");
        Check.That(SettingsPagerChrome.GlyphFor("tab_next", boundary: false)).IsEqualTo("▼");
    }

    [Fact]
    public void GlyphFor_AnUnknownId_Throws()
    {
        Check.ThatCode(() => SettingsPagerChrome.GlyphFor("tab_sideways", boundary: false))
            .Throws<ArgumentOutOfRangeException>();
    }

    // ────── Dwell times ──────

    [Fact]
    public void StepPagerDwellMs_Is450()
    {
        // View-local by settlement: a per-target dwell at the registration site, NOT a new
        // DwellSettings property. 450 is short of the 700 jump so a step is quick to fire and
        // quick to take back.
        Check.That(SettingsPagerChrome.StepPagerDwellMs).IsEqualTo(450);
    }

    [Fact]
    public void DwellMsFor_JumpsUseTheToolbarDwell_StepsThe450Const()
    {
        Check.That(SettingsPagerChrome.DwellMsFor(boundary: true, jumpDwellMs: 700)).IsEqualTo(700);
        Check.That(SettingsPagerChrome.DwellMsFor(boundary: false, jumpDwellMs: 700)).IsEqualTo(450);

        // The jump follows the user's tuning; the step does not.
        Check.That(SettingsPagerChrome.DwellMsFor(boundary: true, jumpDwellMs: 2000)).IsEqualTo(2000);
        Check.That(SettingsPagerChrome.DwellMsFor(boundary: false, jumpDwellMs: 2000)).IsEqualTo(450);
    }

    // ────── Action composition ──────

    [Fact]
    public void ActionFor_ContentDwellStep_IsEmptyBecauseTheViewModelAutoSteps()
    {
        var set = new List<int>();
        var jumped = new List<string>();

        var action = SettingsPagerChrome.ActionFor(
            "pag_next", +1, pageIndex: 1, pageCount: 4,
            vmAutoSteps: true, setPage: set.Add, onJumped: jumped.Add);

        action();

        // The ViewModel's Pagination arm already stepped; a non-empty action would step twice.
        Check.That(set).IsEmpty();
        Check.That(jumped).IsEmpty();
    }

    [Fact]
    public void ActionFor_ContentMouseStep_SetsTheNextPage()
    {
        var set = new List<int>();

        SettingsPagerChrome.ActionFor(
            "pag_next", +1, pageIndex: 1, pageCount: 4,
            vmAutoSteps: false, setPage: set.Add, onJumped: _ => { })();

        Check.That(set).ContainsExactly(2);
    }

    [Fact]
    public void ActionFor_BoundaryJump_SetsTheFarEnd_ThenArmsTheGuard()
    {
        var set = new List<int>();
        var jumped = new List<string>();

        SettingsPagerChrome.ActionFor(
            "pag_prev", -1, pageIndex: 0, pageCount: 4,
            vmAutoSteps: true, setPage: set.Add, onJumped: jumped.Add)();

        // Both the dwell (vmAutoSteps: true) and mouse shapes jump identically; the guard must
        // be armed AFTER the page change, so the re-render the change triggers cannot undo it.
        Check.That(set).ContainsExactly(3);
        Check.That(jumped).ContainsExactly("pag_prev");
    }

    [Fact]
    public void ActionFor_TabStep_CarriesTheStepItself()
    {
        var set = new List<int>();
        var jumped = new List<string>();

        // No ViewModel arm auto-steps tab_* any more (the Tab->Pagination move deleted the
        // special case), so the registered action carries the step on BOTH paths.
        SettingsPagerChrome.ActionFor(
            "tab_next", +1, pageIndex: 0, pageCount: 3,
            vmAutoSteps: false, setPage: set.Add, onJumped: jumped.Add)();

        Check.That(set).ContainsExactly(1);
        Check.That(jumped).IsEmpty();
    }

    // ────── Activation semantics, through the real ViewModel ──────

    private static SettingsWindowViewModel VmWith(int totalPages, int currentPage)
    {
        var vm = QaTestSupport.MakeViewModel();
        vm.Settings.TotalPages = totalPages;
        vm.Settings.CurrentPage = currentPage;
        return vm;
    }

    [Fact]
    public void SimulateActivated_ContentBoundaryPrev_JumpsToTheLastPage()
    {
        var vm = VmWith(totalPages: 4, currentPage: 0);
        vm.Register("pag_prev", DwellCategory.Pagination, 60,
            SettingsPagerChrome.DwellMsFor(boundary: true, jumpDwellMs: 700),
            SettingsPagerChrome.ActionFor(
                "pag_prev", -1, vm.Settings.CurrentPage, vm.Settings.TotalPages,
                vmAutoSteps: true,
                setPage: p => vm.Settings.CurrentPage = p,
                onJumped: _ => { }));

        vm.SimulateActivated("pag_prev", DwellCategory.Pagination);

        // The ViewModel auto-steps first (a no-op at page 0), then runs the registered jump.
        Check.That(vm.Settings.CurrentPage).IsEqualTo(3);
    }

    [Fact]
    public void SimulateActivated_ContentStepNext_StepsOnePage()
    {
        var vm = VmWith(totalPages: 4, currentPage: 1);
        vm.Register("pag_next", DwellCategory.Pagination, 60,
            SettingsPagerChrome.DwellMsFor(boundary: false, jumpDwellMs: 700),
            SettingsPagerChrome.ActionFor(
                "pag_next", +1, vm.Settings.CurrentPage, vm.Settings.TotalPages,
                vmAutoSteps: true,
                setPage: p => vm.Settings.CurrentPage = p,
                onJumped: _ => { }));

        vm.SimulateActivated("pag_next", DwellCategory.Pagination);

        // The empty dwell action means the auto-step is the ONLY step.
        Check.That(vm.Settings.CurrentPage).IsEqualTo(2);
    }

    [Fact]
    public void SimulateActivated_TabBoundaryPrev_JumpsToTheLastTabPage()
    {
        var vm = VmWith(totalPages: 4, currentPage: 0);
        vm.Settings.ShowTab("Cursor");
        var tabPage = 0;
        const int tabPageCount = 3;

        vm.Register("tab_prev", DwellCategory.Pagination, 60,
            SettingsPagerChrome.DwellMsFor(boundary: true, jumpDwellMs: 700),
            SettingsPagerChrome.ActionFor(
                "tab_prev", -1, tabPage, tabPageCount,
                vmAutoSteps: false, setPage: p => tabPage = p, onJumped: _ => { }));

        vm.SimulateActivated("tab_prev", DwellCategory.Pagination);

        Check.That(tabPage).IsEqualTo(2);
        // The tab pager must NOT fall through to ShowTab any more: its ids no longer reach the
        // Tab arm, and ShowTab("tab_prev") would switch to a nonexistent tab.
        Check.That(vm.Settings.CurrentTabName).IsEqualTo("Cursor");
    }

    [Fact]
    public void SimulateActivated_TabStepNext_StepsOneTabPage()
    {
        var vm = VmWith(totalPages: 4, currentPage: 0);
        var tabPage = 0;
        const int tabPageCount = 3;

        vm.Register("tab_next", DwellCategory.Pagination, 60,
            SettingsPagerChrome.DwellMsFor(boundary: false, jumpDwellMs: 700),
            SettingsPagerChrome.ActionFor(
                "tab_next", +1, tabPage, tabPageCount,
                vmAutoSteps: false, setPage: p => tabPage = p, onJumped: _ => { }));

        vm.SimulateActivated("tab_next", DwellCategory.Pagination);

        Check.That(tabPage).IsEqualTo(1);
    }

    // ────── Steps onto a boundary page stay steps (review r1 C1) ──────

    // Mirrors RenderPagination: on every CurrentPage change BOTH content pager buttons
    // re-register with the action computed for the NEW page. A register-once harness (the
    // tests above) cannot see the mid-activation replacement the VM's own auto-step triggers,
    // which is exactly how the step-onto-boundary wrap got past the first suite.
    private static SettingsWindowViewModel ReRegisteringOnPageChange(int total, int current)
    {
        var vm = QaTestSupport.MakeViewModel();
        vm.Settings.TotalPages = total;
        vm.Settings.CurrentPage = current;

        void RegisterBoth()
        {
            foreach (var (id, direction) in new[] { ("pag_prev", -1), ("pag_next", +1) })
            {
                var boundary = SettingsPagerChrome.IsBoundary(
                    vm.Settings.CurrentPage, vm.Settings.TotalPages, direction);
                vm.Register(id, DwellCategory.Pagination, 60,
                    SettingsPagerChrome.DwellMsFor(boundary, jumpDwellMs: 700),
                    SettingsPagerChrome.ActionFor(
                        id, direction, vm.Settings.CurrentPage, vm.Settings.TotalPages,
                        vmAutoSteps: true,
                        setPage: p => vm.Settings.CurrentPage = p,
                        onJumped: _ => { }));
            }
        }

        vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CurrentPage)) RegisterBoth();
        };
        RegisterBoth();
        return vm;
    }

    [Fact]
    public void DwellStep_PrevLandingOnTheFirstPage_LandsThere_NotOnTheWrapJump()
    {
        // The old ordering resolved the registered action AFTER the auto-step, but the step's
        // synchronous re-render had already replaced the registration with the NEW page's
        // action: stepping 1 -> 0 of 4 re-registered pag_prev as the jump to page 3 and ran it.
        var vm = ReRegisteringOnPageChange(total: 4, current: 1);

        vm.SimulateActivated("pag_prev", DwellCategory.Pagination);

        Check.That(vm.Settings.CurrentPage).IsEqualTo(0);
    }

    [Fact]
    public void DwellStep_NextLandingOnTheLastPage_LandsThere_NotOnTheWrapJump()
    {
        var vm = ReRegisteringOnPageChange(total: 4, current: 2);

        vm.SimulateActivated("pag_next", DwellCategory.Pagination);

        Check.That(vm.Settings.CurrentPage).IsEqualTo(3);
    }

    [Fact]
    public void DwellJump_AtABoundary_StillWraps_UnderTheLiveHarness()
    {
        // The jump itself must survive the resolve-before-step reordering: at a boundary the
        // auto-step no-ops (PreviousPage/NextPage clamp), so no re-render fires and the
        // resolved action is still the jump.
        var vm = QaTestSupport.MakeViewModel();
        vm.Settings.TotalPages = 4;
        vm.Settings.CurrentPage = 0;

        var changes = 0;
        var jumped = new List<string>();

        void RegisterBoth()
        {
            foreach (var (id, direction) in new[] { ("pag_prev", -1), ("pag_next", +1) })
            {
                var boundary = SettingsPagerChrome.IsBoundary(
                    vm.Settings.CurrentPage, vm.Settings.TotalPages, direction);
                vm.Register(id, DwellCategory.Pagination, 60,
                    SettingsPagerChrome.DwellMsFor(boundary, jumpDwellMs: 700),
                    SettingsPagerChrome.ActionFor(
                        id, direction, vm.Settings.CurrentPage, vm.Settings.TotalPages,
                        vmAutoSteps: true,
                        setPage: p => vm.Settings.CurrentPage = p,
                        onJumped: jumped.Add));
            }
        }

        vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CurrentPage)) changes++;
        };

        RegisterBoth();
        vm.SimulateActivated("pag_prev", DwellCategory.Pagination);

        Check.That(vm.Settings.CurrentPage).IsEqualTo(3);
        Check.That(jumped).ContainsExactly("pag_prev");
        Check.That(changes).IsEqualTo(1);
    }

    // ────── Pager geometry (the "chrome is unguarded" gap) ──────

    [Fact]
    public void PagerGeometry_IsPinned()
    {
        Check.That(SettingsWindow.PagBtnRadius).IsEqualTo(60);
        Check.That(SettingsWindow.PaginationButtonWidth).IsEqualTo(100);
        Check.That(SettingsWindow.PaginationButtonHeight).IsEqualTo(80);
        Check.That(SettingsWindow.PaginationButtonMargin).IsEqualTo(4);
        Check.That(SettingsWindow.TabPagerButtonMargin).IsEqualTo(14);
        Check.That(SettingsWindow.TabBtnRadius).IsEqualTo(85);
    }

    [Fact]
    public void TabPagerButtons_HitCirclesDoNotOverlapEachOther()
    {
        // Two 100-DIP buttons registering a 60-DIP radius (120-DIP circles) need ≥ 120 between
        // centres; the 14 DIP margin is what buys that: 14 + 100 + 14 = 128.
        var centreDistance = SettingsWindow.TabPagerButtonMargin * 2 + SettingsWindow.PaginationButtonWidth;
        Check.WithCustomMessage(
                "The tab pager's two hit circles overlap: adjacent pager targets whose circles "
                + "intersect make the nearest-target arbitration flip mid-dwell.")
            .That(centreDistance).IsGreaterOrEqualThan(2 * SettingsWindow.PagBtnRadius);
    }
}

/// <summary>
/// Behaviour of the wrap-jump re-arm guard in isolation: a fake registrar records the
/// unregister/re-arm calls, and the gaze is fed directly so the leave-the-circle rule — the
/// guard's ONLY rule, there is no timeout — can be pinned without a window.
/// </summary>
public class SettingsPagerRearmGuardTests
{
    private sealed class RecordingRegistrar : IDwellRegistrar
    {
        public List<string> Registered { get; } = new();
        public List<string> Unregistered { get; } = new();

        public void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs,
            Action onActivated) => Registered.Add(id);

        public void Unregister(string id, DwellCategory category) => Unregistered.Add(id);

        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint center) { }

        public double GetProgress(string id, DwellCategory category) => 0;
    }

    private static readonly PhysicalPoint Centre = new(100, 100);

    [Fact]
    public void Disarm_UnregistersTheTarget_AndHoldsItWhileGazeStaysInside()
    {
        var registrar = new RecordingRegistrar();
        var rearmed = new List<string>();
        var guard = new SettingsPagerRearmGuard(registrar, (id, _) => rearmed.Add(id));

        guard.Disarm("pag_prev", DwellCategory.Pagination, Centre, radiusPhysical: 60);

        Check.That(registrar.Unregistered).ContainsExactly("pag_prev");
        Check.That(guard.IsDisarmed("pag_prev")).IsTrue();

        // A full dwell cycle's worth of frames with the gaze resting dead centre: nothing may
        // re-register. There is no timeout — a rest holds until the gaze actually leaves.
        for (var frame = 0; frame < 400; frame++)
            guard.Update(Centre);

        Check.That(rearmed).IsEmpty();
        Check.That(guard.IsDisarmed("pag_prev")).IsTrue();
    }

    [Fact]
    public void Update_RearmsOnceWhenTheGazeLeavesTheCircle()
    {
        var registrar = new RecordingRegistrar();
        var rearmed = new List<string>();
        var guard = new SettingsPagerRearmGuard(registrar, (id, _) => rearmed.Add(id));
        guard.Disarm("pag_prev", DwellCategory.Pagination, Centre, radiusPhysical: 60);

        // 61 DIP away from a 60 DIP radius: outside.
        guard.Update(new PhysicalPoint(100, 161));

        Check.That(rearmed).ContainsExactly("pag_prev");
        Check.That(guard.IsDisarmed("pag_prev")).IsFalse();

        // And it does not fire again on later frames — the caller re-registers once.
        guard.Update(new PhysicalPoint(100, 161));
        Check.That(rearmed).ContainsExactly("pag_prev");
    }

    [Fact]
    public void Update_NeverRearmsWhileTheGazeRests_ThereIsNoHorizon()
    {
        // The dropped 2x-jump-dwell fallback re-armed a resting user after 1.4 s — exactly the
        // case the guard exists to suppress, reopening the walk at step rate. Its replacement
        // is nothing at all: a frozen gaze stream sends no frames, and without frames nothing
        // accrues dwell anywhere, so a never-re-arming hold costs nothing. Pinned over more
        // consecutive rest frames than any real rest needs.
        var registrar = new RecordingRegistrar();
        var rearmed = new List<string>();
        var guard = new SettingsPagerRearmGuard(registrar, (id, _) => rearmed.Add(id));
        guard.Disarm("pag_prev", DwellCategory.Pagination, Centre, radiusPhysical: 60);

        for (var frame = 0; frame < 400; frame++)
            guard.Update(Centre);

        Check.That(rearmed).IsEmpty();
        Check.That(guard.IsDisarmed("pag_prev")).IsTrue();
    }

    [Fact]
    public void ClearContentPager_DropsContentPagerGuards_WithoutReRegistering()
    {
        var registrar = new RecordingRegistrar();
        var rearmed = new List<string>();
        var guard = new SettingsPagerRearmGuard(registrar, (id, _) => rearmed.Add(id));
        guard.Disarm("pag_prev", DwellCategory.Pagination, Centre, radiusPhysical: 60);

        guard.ClearContentPager();

        Check.That(guard.IsDisarmed("pag_prev")).IsFalse();
        guard.Update(new PhysicalPoint(100, 161));
        Check.That(rearmed).IsEmpty();
    }

    [Fact]
    public void ClearContentPager_LeavesTheTabPagerGuardsHeld()
    {
        // A CONTENT re-render to one page must not drop a held tab-pager guard: the clear
        // would leave the tab button unregistered — dead to dwell — until its next
        // re-registration. The two pagers' guards have independent lifecycles.
        var registrar = new RecordingRegistrar();
        var rearmed = new List<string>();
        var guard = new SettingsPagerRearmGuard(registrar, (id, _) => rearmed.Add(id));
        guard.Disarm("tab_prev", DwellCategory.Pagination, Centre, radiusPhysical: 60);

        guard.ClearContentPager();

        Check.That(guard.IsDisarmed("tab_prev")).IsTrue();
        guard.Update(Centre);
        Check.That(rearmed).IsEmpty();
    }
}

/// <summary>
/// The rider's behavioural claim, end to end through a real
/// <see cref="SettingsWindowViewModel"/>: a gaze that rests on the button it just jumped with
/// turns the page exactly ONCE — no oscillation walk. The harness mirrors the view's pager
/// wiring (register through the production helpers, re-register on every page change like
/// <c>RenderPagination</c>, guard the jump through <see cref="SettingsPagerRearmGuard"/>) so the
/// walk a missing guard would produce is the one production would produce.
/// </summary>
public class SettingsPagerRestingGazeTests
{
    private const int JumpDwellMs = 700;   // ToolbarButtonDwellMs default, the jump dwell
    private const double RadiusPhysical = 60;
    private static readonly PhysicalPoint Centre = new(500, 500);
    private static readonly PhysicalPoint Away = new(5000, 5000);

    [Fact]
    public void RestingGazeOnAJumpedButton_TurnsThePageExactlyOnce()
    {
        var vm = QaTestSupport.MakeViewModel();
        vm.Settings.TotalPages = 4;
        vm.Settings.CurrentPage = 0;

        var changes = 0;
        SettingsPagerRearmGuard? guard = null;

        void RegisterPrev()
        {
            if (guard!.IsDisarmed("pag_prev")) return;
            var boundary = SettingsPagerChrome.IsBoundary(
                vm.Settings.CurrentPage, vm.Settings.TotalPages, -1);
            vm.Register("pag_prev", DwellCategory.Pagination, RadiusPhysical,
                SettingsPagerChrome.DwellMsFor(boundary, JumpDwellMs),
                SettingsPagerChrome.ActionFor(
                    "pag_prev", -1, vm.Settings.CurrentPage, vm.Settings.TotalPages,
                    vmAutoSteps: true,
                    setPage: p => vm.Settings.CurrentPage = p,
                    onJumped: id => guard!.Disarm(id, DwellCategory.Pagination, Centre, RadiusPhysical)));
            vm.UpdatePosition("pag_prev", DwellCategory.Pagination, Centre);
        }

        guard = new SettingsPagerRearmGuard(vm, (_, _) => RegisterPrev());
        vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.CurrentPage)) return;
            changes++;
            // RenderPagination re-registers the pager on every page change; the jump's own
            // re-registration happens here, BEFORE the action arms the guard.
            RegisterPrev();
        };
        RegisterPrev();

        // Resting gaze at 30 fps for over six seconds: the jump fires once (~726 ms), then the
        // guard holds the button with NO time limit — this deliberately runs far past the 1.4 s
        // horizon where the dropped 2x fallback used to re-arm under the user's motionless
        // gaze and reopen the walk. Unguarded, the step-back cycle runs 3->2->1->0, and the
        // step onto 0 is where the boundary button jumps back to 3 — repeating for as long as
        // the gaze rests.
        for (var frame = 0; frame < 200; frame++)
        {
            guard.Update(Centre);
            vm.Receive(new GazeCursorMovedMessage(Centre, frame * 33_000L));
        }

        Check.That(vm.Settings.CurrentPage).IsEqualTo(3);
        Check.WithCustomMessage(
                "A gaze resting on the button it just wrapped with changed the page {0} times. "
                + "The re-arm guard must hold the jumped button disarmed until the gaze leaves "
                + "its own hit circle — there is no timeout — or the same rest walks the list: "
                + "0 -> last -> last-1 -> ...")
            .That(changes).IsEqualTo(1);
    }

    [Fact]
    public void GazeLeavingAndReturning_RearmsTheButton_AndOneMoreDwellSteps()
    {
        var vm = QaTestSupport.MakeViewModel();
        vm.Settings.TotalPages = 4;
        vm.Settings.CurrentPage = 0;

        var changes = 0;
        SettingsPagerRearmGuard? guard = null;

        void RegisterPrev()
        {
            if (guard!.IsDisarmed("pag_prev")) return;
            var boundary = SettingsPagerChrome.IsBoundary(
                vm.Settings.CurrentPage, vm.Settings.TotalPages, -1);
            vm.Register("pag_prev", DwellCategory.Pagination, RadiusPhysical,
                SettingsPagerChrome.DwellMsFor(boundary, JumpDwellMs),
                SettingsPagerChrome.ActionFor(
                    "pag_prev", -1, vm.Settings.CurrentPage, vm.Settings.TotalPages,
                    vmAutoSteps: true,
                    setPage: p => vm.Settings.CurrentPage = p,
                    onJumped: id => guard!.Disarm(id, DwellCategory.Pagination, Centre, RadiusPhysical)));
            vm.UpdatePosition("pag_prev", DwellCategory.Pagination, Centre);
        }

        guard = new SettingsPagerRearmGuard(vm, (_, _) => RegisterPrev());
        vm.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.CurrentPage)) return;
            changes++;
            RegisterPrev();
        };
        RegisterPrev();

        // 30 frames: the jump fires and the guard arms; the gaze is still resting.
        var t = 0L;
        for (var frame = 0; frame < 30; frame++, t += 33_000)
        {
            guard.Update(Centre);
            vm.Receive(new GazeCursorMovedMessage(Centre, t));
        }
        Check.That(vm.Settings.CurrentPage).IsEqualTo(3);
        Check.That(changes).IsEqualTo(1);

        // Two frames with the gaze away: the guard re-arms the button as a step-back.
        for (var frame = 0; frame < 2; frame++, t += 33_000)
        {
            guard.Update(Away);
            vm.Receive(new GazeCursorMovedMessage(Away, t));
        }

        // Back on the button: one more dwell steps back exactly one page (3 -> 2), then the
        // guard is not involved — this is an ordinary step, not a jump.
        for (var frame = 0; frame < 40; frame++, t += 33_000)
        {
            guard.Update(Centre);
            vm.Receive(new GazeCursorMovedMessage(Centre, t));
        }

        Check.That(vm.Settings.CurrentPage).IsEqualTo(2);
        Check.That(changes).IsEqualTo(2);
    }
}

/// <summary>
/// Source scans for the pieces of the pager wiring that live in the un-constructible
/// <see cref="SettingsWindow"/>: the per-frame guard update, the guard-aware registration, the
/// jump arming, and the dead-code pairing on the ViewModel side.
/// </summary>
public class SettingsPagerWiringTests
{
    private static string WindowSource() => File.ReadAllText(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Views", "SettingsWindow.xaml.cs"));

    private static string ViewModelSource() => File.ReadAllText(Path.Combine(
        SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.ViewModels", "SettingsWindowViewModel.cs"));

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"SettingsWindow.xaml.cs no longer declares `{signature}`. "
                + "Repoint this scan at wherever it moved; do not delete it.")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Check.WithCustomMessage($"The `{signature}` body is unterminated.")
            .That(end).IsStrictlyGreaterThan(start);
        return source[start..end];
    }

    [Fact]
    public void UpdateGaze_FeedsTheGuardEveryFrame()
    {
        var body = ExtractMethod(WindowSource(), "public void UpdateGaze(");
        Check.WithCustomMessage(
                "UpdateGaze no longer feeds the re-arm guard. The guard only learns the gaze has "
                + "left a jumped button from this per-frame call, so without it a resting gaze "
                + "walks the page list.")
            .That(body).Contains("_pagerRearmGuard.Update(");
    }

    [Fact]
    public void JumpingPagerButtons_ArmTheGuard()
    {
        var body = ExtractMethod(WindowSource(), "private void GuardJumpedPager(");
        Check.WithCustomMessage(
                "GuardJumpedPager no longer disarms the jumped button. The wrap-jump's whole "
                + "anti-oscillation rider lives here.")
            .That(body).Contains("_pagerRearmGuard.Disarm(");
    }

    [Fact]
    public void GuardJumpedPager_DisarmsWithTheTrackedLaidOutCentre()
    {
        var body = ExtractMethod(WindowSource(), "private void GuardJumpedPager(");
        Check.WithCustomMessage(
                "GuardJumpedPager reads its centre off the CURRENT pager Border first again. "
                + "The jump's own re-render replaced that Border before this runs, and a fresh "
                + "border has had no layout pass: PointToScreen returns the panel origin, "
                + "70-240 DIP off the real centre, the guard re-arms on the first frame, and "
                + "the oscillation walk returns (review r1 H1). Read _pagerCenters — the "
                + "position loop's last laid-out centre — first; the border read is only the "
                + "never-pushed-yet fallback.")
            .That(body.Contains("_pagerCenters.TryGetValue")).IsTrue();
    }

    [Fact]
    public void RenderPagination_NeverClearsTheTrackedPagerCentres()
    {
        var body = ExtractMethod(WindowSource(), "private void RenderPagination(");
        Check.WithCustomMessage(
                "RenderPagination touches _pagerCenters. The jump's setPage runs this re-render "
                + "BEFORE GuardJumpedPager reads the tracked centre, so clearing the dictionary "
                + "here pushes the guard back onto the fresh-border read it must not use. The "
                + "dictionary is cumulative by design; stale entries are harmless.")
            .That(body.Contains("_pagerCenters")).IsFalse();
    }

    [Fact]
    public void TryUpdatePosition_RecordsOnlyLaidOutPaginationCentres()
    {
        var body = ExtractMethod(WindowSource(), "private void TryUpdatePosition(");
        Check.WithCustomMessage(
                "TryUpdatePosition no longer records pager centres for the re-arm guard. "
                + "Without the record, GuardJumpedPager falls back to reading the freshly "
                + "re-rendered border — the panel-origin garbage of review r1 H1.")
            .That(body.Contains("_pagerCenters[id] =")).IsTrue();

        // The record must be gated on actual layout: ActualWidth/Height are set by Arrange,
        // together with the VisualOffsets PointToScreen needs. An ungated record would let a
        // border that is in the tree but not yet arranged overwrite the previous border's good
        // centre with panel-origin garbage.
        Check.WithCustomMessage(
                "TryUpdatePosition records pager centres without checking the border is laid "
                + "out (ActualWidth/Height > 0). A pre-arrange frame would overwrite the good "
                + "centre with panel-origin garbage.")
            .That(body.Contains("ActualWidth > 0")).IsTrue();
    }

    [Fact]
    public void PagerRegistrations_GoThroughTheGuardAwareHelper()
    {
        var body = ExtractMethod(WindowSource(), "private void RegisterPagerTarget(");
        Check.WithCustomMessage(
                "RegisterPagerTarget no longer consults the guard before registering. A jump "
                + "re-renders the page and re-registers its buttons; a blind Register silently "
                + "undoes the guard on the very frame it was armed.")
            .That(body).Contains("IsDisarmed(");

        // And the jump re-render path really does use it: MakePagButton registers through the
        // helper, and never inline (an inline Register would bypass the guard).
        var make = ExtractMethod(WindowSource(), "private Border MakePagButton(");
        Check.That(make).Contains("RegisterContentPagerTarget(");
        Check.That(make).Not.Contains("_viewModel.Register(");
    }

    [Fact]
    public void TabPager_RegistersUnderPagination()
    {
        var body = ExtractMethod(WindowSource(), "private void RegisterTabPagerTarget(");
        Check.WithCustomMessage(
                "The tab pager no longer registers through RegisterPagerTarget — its targets "
                + "must ride DwellCategory.Pagination (the Tab->Pagination move took the tab "
                + "flip off the shared 400 ms Tab detector).")
            .That(body).Contains("RegisterPagerTarget(");

        var positions = ExtractMethod(WindowSource(), "private void UpdateAllTargetPositions(");
        Check.WithCustomMessage(
                "UpdateAllTargetPositions no longer pushes the tab pager's centres under "
                + "Pagination. The registered targets would stay at (0,0) and never accumulate.")
            .That(positions).Contains("_tabPagerButtons, DwellCategory.Pagination");
    }

    [Fact]
    public void DwellConstants_AreAppliedAtTheRegistrationSites()
    {
        foreach (var signature in new[]
                 {
                     "private void RegisterContentPagerTarget(",
                     "private void RegisterTabPagerTarget(",
                 })
        {
            var body = ExtractMethod(WindowSource(), signature);
            Check.WithCustomMessage(
                    $"{signature} no longer derives its dwell from SettingsPagerChrome.DwellMsFor "
                    + "— the 450 step / 700 jump split must come from the one shared helper.")
                .That(body).Contains("SettingsPagerChrome.DwellMsFor(");
        }
    }

    [Fact]
    public void PagerGlyphsAndActions_ComeFromTheBoundaryHelper()
    {
        // A glyph or an action hand-rolled at the call site would drift from the helper the
        // behavioural tests pin. Both pagers' glyphs come from GlyphFor, and all four
        // action sites (mouse + dwell, content + tab) from ActionFor.
        Check.That(ExtractMethod(WindowSource(), "private Border MakePagButton("))
            .Contains("SettingsPagerChrome.GlyphFor(");
        Check.That(ExtractMethod(WindowSource(), "private void UpdateTabPagerButton("))
            .Contains("SettingsPagerChrome.GlyphFor(");

        foreach (var signature in new[]
                 {
                     "private void ActivateContentPager(",
                     "private void RegisterContentPagerTarget(",
                     "private void ActivateTabPager(",
                     "private void RegisterTabPagerTarget(",
                 })
        {
            Check.WithCustomMessage($"{signature} no longer composes its action through "
                    + "SettingsPagerChrome.ActionFor — the boundary jump/step mapping must live "
                    + "in one place.")
                .That(ExtractMethod(WindowSource(), signature)).Contains("SettingsPagerChrome.ActionFor(");
        }
    }

    [Fact]
    public void NoDimmedBoundaryButtonsRemain()
    {
        // The 0.3-opacity idiom marked a boundary button dead. Boundary buttons are live
        // wrap-jumps now, so any surviving dim site is a lie the user can dwell on.
        Check.WithCustomMessage(
                "SettingsWindow.xaml.cs still dims a boundary button (Opacity = 0.3). Boundary "
                + "buttons are wrap-jumps now; remove the dim rather than re-document it.")
            .That(WindowSource()).Not.Contains("Opacity = 0.3");
    }

    [Fact]
    public void RenderPagination_UnregistersTheContentPagerTargets_WhenTheBarHides()
    {
        var body = ExtractMethod(WindowSource(), "private void RenderPagination(");
        Check.WithCustomMessage(
                "RenderPagination's single-page branch no longer unregisters pag_prev/pag_next. "
                + "A stale registration keeps a live hit circle at its last pushed centre, and "
                + "since the wrap-jump change its action can set CurrentPage out of range — a "
                + "dwell on vanished chrome blanks the content page with no pager to leave by "
                + "(review r1 M1).")
            .That(body.Contains("Unregister(\"pag_prev\"")
                && body.Contains("Unregister(\"pag_next\"")).IsTrue();
    }

    [Fact]
    public void ThePagerBarHide_ScopesTheGuardClearToTheContentPager()
    {
        // The guard's reset at the same site must not drop the TAB pager's guards with it: a
        // clear drops the held state WITHOUT re-registering, so an unscoped clear would leave
        // a guarded tab button unregistered — dead to dwell — until its next tab-page change
        // (review r1 L1).
        var body = ExtractMethod(WindowSource(), "private void RenderPagination(");
        Check.WithCustomMessage(
                "RenderPagination's single-page branch calls an unscoped guard Clear again. "
                + "It must use ClearContentPager — the tab pager's guards keep their own "
                + "lifecycle.")
            .That(body.Contains("_pagerRearmGuard.ClearContentPager()")).IsTrue();
        Check.WithCustomMessage(
                "RenderPagination's single-page branch still calls an unscoped guard Clear(). "
                + "Use ClearContentPager().")
            .That(body.Contains("_pagerRearmGuard.Clear()")).IsFalse();
    }

    [Fact]
    public void DeadTabPagerSpecialCase_IsGoneFromTheViewModel()
    {
        // The pairing: the view no longer registers tab_* under Tab, so the Tab arm's special
        // case would be dead code. Its pinning test was deleted with it.
        Check.WithCustomMessage(
                "SettingsWindowViewModel still special-cases tab_prev/tab_next in the Tab arm. "
                + "The tab pager registers under Pagination now, so this arm can never see those "
                + "ids — delete the dead branch (and do not re-add its test).")
            .That(ViewModelSource()).Not.Contains("\"tab_prev\"");
    }
}

/// <summary>
/// The WPF premise the re-arm guard's centre capture rests on, pinned so it cannot silently
/// flip: a pager Border that a re-render has just re-added — but that has had NO layout pass —
/// does not read back its laid-out position. PointToScreen answers the panel origin (its
/// ActualWidth/ActualHeight are still 0 and its VisualOffsets are unset), which is the
/// ~70–240 DIP error review r1's H1 caught <c>GuardJumpedPager</c> arming the guard on. The
/// production fix reads the position loop's last LAID-OUT centre instead
/// (<c>SettingsWindow._pagerCenters</c>, held by the wiring pins above); this probe reproduces
/// the sequence — lay out, clear, re-add, read — with the pager row's real geometry.
///
/// <para>
/// Nothing is shown: the T1 off-screen <c>WS_POPUP</c> HwndSource path, parked at -30000 and
/// never made visible. No UI category, so CI runs it.
/// </para>
/// </summary>
public class SettingsPagerFreshBorderProbeTests
{
    [StaFact]
    public void AFreshlyReAddedPagerBorder_DoesNotReadBackItsLaidOutCentre()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var root = new Grid { Width = 1000, Height = 200 };
        root.Children.Add(panel);

        Border MakePagerButton(string glyph) => new()
        {
            Width = SettingsWindow.PaginationButtonWidth,
            Height = SettingsWindow.PaginationButtonHeight,
            Margin = new Thickness(SettingsWindow.PaginationButtonMargin),
            Child = new TextBlock { Text = glyph },
        };

        // RenderPagination's row: button, page indicator (MinWidth 80), button.
        panel.Children.Add(MakePagerButton("◀"));
        panel.Children.Add(new TextBlock
        {
            Text = "1 / 4",
            MinWidth = 80,
            TextAlignment = TextAlignment.Center,
        });
        panel.Children.Add(MakePagerButton("▶"));

        using var source = new HwndSource(new HwndSourceParameters("pager-fresh-border-probe")
        {
            WindowStyle = unchecked((int)0x80000000),
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });
        source.RootVisual = root;
        root.Measure(new Size(1000, 200));
        root.Arrange(new Rect(0, 0, 1000, 200));
        root.UpdateLayout();

        var laidOut = (Border)panel.Children[0];
        var trueCentre = laidOut.PointToScreen(
            new Point(laidOut.ActualWidth / 2, laidOut.ActualHeight / 2));

        // The jump's re-render: clear and re-add fresh borders, then read the centre the way
        // GuardJumpedPager used to — with NO layout pass in between.
        panel.Children.Clear();
        var fresh = MakePagerButton("⏭");
        panel.Children.Add(fresh);
        panel.Children.Add(new TextBlock
        {
            Text = "4 / 4",
            MinWidth = 80,
            TextAlignment = TextAlignment.Center,
        });
        panel.Children.Add(MakePagerButton("▶"));

        Check.WithCustomMessage(
                "The freshly re-added border was laid out before the read (ActualWidth "
                + $"'{fresh.ActualWidth}'), so this probe no longer reproduces the pre-arrange "
                + "state it exists to pin — the sequence above changed.")
            .That(fresh.ActualWidth).IsEqualTo(0);

        var freshCentre = fresh.PointToScreen(
            new Point(fresh.ActualWidth / 2, fresh.ActualHeight / 2));

        Check.WithCustomMessage(
                $"A freshly re-added pager Border read back {freshCentre} against the laid-out "
                + $"{trueCentre}: WPF now answers correctly before a layout pass. The "
                + "fresh-border fallback in GuardJumpedPager is no longer wrong — revisit the "
                + "wiring pins that hold the tracked-centre mechanism in place.")
            .That(freshCentre).IsNotEqualTo(trueCentre);
    }
}
