namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// The note gate's OUTPUT, not the suspension flag: a behavioural pin on the exact composed
/// predicate <see cref="EventCoordinator.Initialize"/> hands to
/// <c>NoteManager.SetSuspendPredicate</c>. It runs a REAL <see cref="ActivationOfferCoordinator"/>
/// (so <c>ModalSuspended</c> is computed by the production suspension logic) through the same
/// <see cref="EventCoordinator.IsNoteSuspended"/> function production calls.
///
/// <para>
/// <b>Deliberate redundancy — do not delete either path believing the other is unpinned.</b>
/// Notes are kept out from under Backstage by TWO independent terms: the offer coordinator's
/// <c>ModalSuspended</c> peer flag and the direct <c>_modal.BackstageOpen</c> term. The mutation
/// table this test was written to prove:
/// </para>
/// <list type="bullet">
/// <item>drop the direct <c>|| _modal.BackstageOpen</c> term ALONE → <b>GREEN</b>: ModalSuspended
/// still carries Backstage, and this test still sees true.</item>
/// <item>move the backstage reason into the offers-only bucket (so ModalSuspended no longer holds)
/// ALONE → <b>GREEN</b>: the direct term still carries it.</item>
/// <item>BOTH together → <b>RED</b>: neither path remains.</item>
/// </list>
/// <para>
/// A later cleanup sees the direct term as "redundant with ModalSuspended" and the flag as
/// "redundant with the direct term". Each single deletion is invisible to every behavioural test
/// in the suite; only removing both is observable. That is why the redundancy is recorded here and
/// in <see cref="EventCoordinator.IsNoteSuspended"/>'s own comment. The two single-path mutants
/// must be run in their own builds (NFluent stops at the first failing check, so a batch cannot
/// attribute a GREEN); only the both-together RED may share one build.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class NoteGateBackstagePredicateTests
{
    private sealed class NoOpQuickActionsStore : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => Array.Empty<QuickAction>();
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => null;
    }

    [StaFact]
    public void TheComposedPredicate_IsFalseBefore_TrueUnderBackstage_FalseAfter()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var gaze = new GazeEngine(
            new TestFakes.FakeGazeProvider(), new TestFakes.FakeInputSimulator(), settings);
        using var _ = gaze;

        var modal = new ModalInteractionState();
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            GazeEngine = gaze,
            InteractionManager = gaze.InteractionManager,
            Settings = settings,
            Messenger = messenger,
            Dispatcher = Dispatcher.CurrentDispatcher,
            KeyboardVM = new KeyboardViewModel(settings, messenger),
            ActionMenuVM = new ActionMenuViewModel(settings, messenger),
            ModesMenuVM = new ModesMenuViewModel(settings, messenger),
            TaskSwitcherVM = new TaskSwitcherViewModel(
                new TestFakes.FakeWindowActionManager(), settings, messenger),
            QuickActionsGridVM = new QuickActionsGridViewModel(
                new NoOpQuickActionsStore(), settings, messenger),
        };

        var offers = new ActivationOfferCoordinator(
            new ToolbarLifecycleCoordinator(modal), modal,
            _ => Array.Empty<PhysicalRect>());
        offers.Initialize(context);
        try
        {
            // The exact expression production stores on the NoteManager (the source pin in
            // EventCoordinatorTests keeps the wiring to this call).
            Func<bool> predicate = () => EventCoordinator.IsNoteSuspended(
                offers,
                context.InteractionManager.CurrentState,
                context.ModesMenuVM.IsVisible,
                modal.BackstageOpen);

            // Vacuity guard: a predicate that starts true would pass the "true under Backstage"
            // assertion for the wrong reason.
            Check.WithCustomMessage("the note gate must be open before Backstage")
                .That(predicate()).IsFalse();

            modal.BackstageOpen = true;   // raises BackstageOpenChanged → RecomputeSuspension

            Check.WithCustomMessage("the composed predicate must suspend notes under Backstage")
                .That(predicate()).IsTrue();

            modal.BackstageOpen = false;

            Check.WithCustomMessage("closing Backstage must reopen the note gate")
                .That(predicate()).IsFalse();
        }
        finally
        {
            offers.Dispose();
        }
    }
}
