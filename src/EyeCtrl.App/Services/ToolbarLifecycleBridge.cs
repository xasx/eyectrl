namespace EyeCtrl.App.Services;

using EyeCtrl.App.Services.Coordinators;

/// <summary>
/// The late-bound hand-off that carries the EventCoordinator-owned
/// <see cref="ToolbarLifecycleCoordinator"/> (constructed inside
/// <c>EventCoordinator.Initialize</c> and never registered in DI) to the lazily-resolved
/// <see cref="AdaptationPreviewService"/> (P9: the service — and with it the preview window — must
/// not be resolved at startup, so it cannot constructor-inject the coordinator).
/// <para>
/// App.xaml.cs attaches the live coordinator immediately after EventCoordinator initializes, which
/// is strictly before <c>AdaptationActivation</c> can first fire (its <c>Start</c> runs later in
/// OnStartup), so a null <see cref="Coordinator"/> at use time is a wiring defect. It is logged
/// loudly rather than thrown — a comfort feature must not take the session down (D14).
/// </para>
/// </summary>
internal sealed class ToolbarLifecycleBridge
{
    private ToolbarLifecycleCoordinator? _coordinator;

    /// <summary>Hands over the live coordinator. Called once from App startup, after
    /// EventCoordinator.Initialize.</summary>
    public void Attach(ToolbarLifecycleCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <summary>The attached coordinator, or null before App wiring has run.</summary>
    public ToolbarLifecycleCoordinator? Coordinator => _coordinator;
}
