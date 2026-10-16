namespace EyeCtrl.App.Tests.Fixtures;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Shared toolbar construction helpers. Both <see cref="EyeCtrl.App.Tests.ToolbarActivationFlashTests"/>
/// and <see cref="EyeCtrl.App.Tests.ToolbarButtonVisualTests"/> need a
/// <see cref="WpfApplicationGate"/>-initialised, <c>Initialize()</c>'d <see cref="ToolbarWindow"/>
/// bound to a fresh <see cref="ToolbarViewModel"/> — the four-line prologue (construct, Initialize,
/// set DataContext, BindToViewModel) was inlined at every call site (once per test class, and
/// repeatedly within <c>ToolbarButtonVisualTests</c>) before being promoted here.
/// </summary>
internal static class ToolbarTestHelpers
{
    /// <summary>
    /// The toolbar button diameter these tests build against — the project's current default
    /// (<see cref="ToolbarSettings.Size"/>), not an arbitrary test value. Several assertions (e.g.
    /// the band's pitch arithmetic in <c>ToolbarButtonVisualTests</c>) are derived from it.
    /// </summary>
    internal const double ButtonSize = 120;

    /// <summary>
    /// Constructs a <see cref="ToolbarWindow"/>, initialises it at <see cref="ButtonSize"/>, and
    /// binds it to a fresh <see cref="ToolbarViewModel"/>. Ensures the shared WPF
    /// <see cref="System.Windows.Application"/> exists first via <see cref="WpfApplicationGate"/>.
    ///
    /// <para>
    /// <paramref name="includeReset"/> defaults FALSE — production parity: the launched app's
    /// band carries Reset only while <c>AdaptationSettings.Enabled</c> is on, and that is off by
    /// default. Tests that need the Reset button pass <c>true</c> DIRECTLY (fixture policy
    /// R2-M3) rather than flipping <c>AdaptationSettings.Enabled</c>, which in the launched app
    /// starts the whole adaptation runtime and is not what a band-layout test means to turn on.
    /// Defaulting to the six-button band also makes a forgotten flag fail LOUDLY —
    /// <c>ButtonForTest("ResetAdaptation")</c> throws on the absent id — instead of silently
    /// testing a seven-button band the default app never shows.
    /// </para>
    /// </summary>
    // NOTE: the ToolbarViewModel ctor reads DpiHelper, so every caller of this helper is a
    // DpiHelper reader. Callers sit in the WpfApplication collection and cannot also join
    // "DpiHelper Tests (App)" — and need not: both are DisableParallelization, which xUnit v3
    // runs exclusively, so no writer overlaps them. See AppDpiHelperTestCollection's doc.
    internal static (ToolbarWindow Window, ToolbarViewModel Vm) NewBoundToolbar(
        int dwellTimeMs = 700, bool includeReset = false)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ToolbarWindow();
        window.Initialize(ButtonSize, dwellTimeMs, includeReset);
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.DataContext = vm;
        window.BindToViewModel();
        return (window, vm);
    }
}
