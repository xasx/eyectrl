namespace EyeCtrl.App.Services;

using System;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Wires the manual escape (design §8.6) and the learned-only reset (§10): registers for <see cref="ResetAdaptationMessage"/> (sent by the
/// toolbar reset button and the tray item) and clears all momentary adaptation back to baseline via
/// <see cref="AdaptationCoordinator.Reset"/>. A dedicated small service rather than a handler inside the
/// ctor-less, ~1500-line <c>EventCoordinator</c>, so the reset path is self-contained and headless-testable.
/// </summary>
public sealed class AdaptationResetHandler : IDisposable
{
    private readonly IMessenger _messenger;
    private readonly AdaptationCoordinator _coordinator;
    private readonly AdaptationLearner _learner;

    /// <summary>Initializes the handler over the app messenger and the coordinator it resets.</summary>
    /// <param name="messenger">The app messenger (<c>WeakReferenceMessenger.Default</c> at runtime).</param>
    /// <param name="coordinator">The coordinator whose <see cref="AdaptationCoordinator.Reset"/> is invoked.</param>
    /// <param name="learner">The learned layer the learned-only reset message clears (§10 ResetLearnedBaseline).</param>
    public AdaptationResetHandler(IMessenger messenger, AdaptationCoordinator coordinator, AdaptationLearner learner)
    {
        _messenger = messenger;
        _coordinator = coordinator;
        _learner = learner;
    }

    /// <summary>Registers both reset-message subscriptions. Call once at startup.</summary>
    public void Start()
    {
        _messenger.Register<AdaptationResetHandler, ResetAdaptationMessage>(this, static (h, _) => h._coordinator.Reset());
        _messenger.Register<AdaptationResetHandler, ResetLearnedOffsetsMessage>(this, static (h, _) => h._learner.ResetLearned());
    }

    /// <summary>Unregisters the subscription.</summary>
    public void Dispose() => _messenger.UnregisterAll(this);
}
