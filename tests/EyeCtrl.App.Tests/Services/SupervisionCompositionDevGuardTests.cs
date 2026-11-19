namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;

/// <summary>
/// The dev-build fence around the HKCU Run key: <c>Run\EyeCtrl</c> is shared machine state the
/// installed build owns, and a dev build's reconciler must touch it NEVER — registering would
/// point the owner's logon at a worktree exe, unregistering would delete the owner's real
/// registration (a lockout at the next logon). The test host IS a dev build (its exe is never
/// directly under <c>Program Files\EyeCtrl*</c>), so the guard is exercised exactly as production
/// would run it.
/// <para>
/// Mutant this must catch: the dev guard dropped, so the reconciler runs in a dev build → the
/// recording registrar receives calls → RED.
/// </para>
/// </summary>
public class SupervisionCompositionDevGuardTests
{
    [Fact]
    public void RunKeyReconciler_InADevBuild_TouchesNoRegistration_EitherWay()
    {
        var settings = new EyeCtrlSettings();
        var registrar = new RecordingRegistrar();
        var reconcile = SupervisionComposition.CreateRunKeyReconciler(settings, registrar, () => false);

        // BOTH arms, so a mutant that drops the guard fails on whichever arm the policy allows:
        // StartOnBoot=true would REGISTER the dev exe, false would UNREGISTER the owner's value.
        settings.General.StartOnBoot = true;
        reconcile();
        settings.General.StartOnBoot = false;
        reconcile();

        Check.That(registrar.Calls).IsEmpty();
    }

    [Fact]
    public void RunKeyReconciler_InADevBuild_AlsoSkipsTheProductionFactory()
    {
        // The production factory is what App.xaml.cs actually calls; it binds the real
        // registration probe but must defer to the same dev guard.
        var settings = new EyeCtrlSettings();
        settings.General.StartOnBoot = true;
        var registrar = new RecordingRegistrar();

        SupervisionComposition.CreateProductionRunKeyReconciler(settings, registrar)();

        Check.That(registrar.Calls).IsEmpty();
    }

    private sealed class RecordingRegistrar : IStartupRegistrar
    {
        public List<bool> Calls { get; } = [];

        public void SetStartOnBoot(bool enabled) => Calls.Add(enabled);

        public bool IsRegistered => false;
    }
}
