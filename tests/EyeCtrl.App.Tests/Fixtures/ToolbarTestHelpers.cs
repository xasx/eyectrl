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
    /// </summary>
    internal static (ToolbarWindow Window, ToolbarViewModel Vm) NewBoundToolbar(int dwellTimeMs = 700)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ToolbarWindow();
        window.Initialize(ButtonSize, dwellTimeMs);
        var vm = new ToolbarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        window.DataContext = vm;
        window.BindToViewModel();
        return (window, vm);
    }
}
