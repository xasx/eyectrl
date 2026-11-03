namespace EyeCtrl.Sdk.Wpf.Tests;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EyeCtrl.Contracts;
using EyeCtrl.Sdk.Abstractions;
using EyeCtrl.Sdk.Wpf;

public class WpfElementAdapterTests
{
    [StaFact]
    public void Mode_ReflectsAttachedPropertyChanges()
    {
        var btn = new Button();
        Dwell.SetMode(btn, DwellMode.Focus);
        var adapter = new WpfElementAdapter("t1", btn);

        Check.That(adapter.Mode).IsEqualTo(DwellMode.Focus);

        Dwell.SetMode(btn, DwellMode.Invoke);
        Check.That(adapter.Mode).IsEqualTo(DwellMode.Invoke);
    }

    /// <summary>Drains queued dispatcher work (Activate's BeginInvoke) — the same pump used by
    /// EyeCareHooksHeadlessTests in EyeCtrl.App.Tests. Nothing is shown anywhere here: the
    /// Buttons stay detached from any visual tree.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Records Execute calls so the test can prove the ButtonBase command fallback did
    /// NOT run when the UIA Invoke provider already handled the activation.</summary>
    private sealed class RecordingCommand : ICommand
    {
        public int ExecuteCount;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => ExecuteCount++;
    }

    // TryGetScreenRect must return FALSE for a detached element: the dwell engine hit-tests this
    // rect for dwell targeting — a false-yes would let dwell fire on junk geometry. For this
    // input BOTH the unloaded/zero-size guard (:38) and the PointToScreen catch (:46) answer
    // false, so the fact pins the integrated contract; separating them would need a loaded
    // zero-size element, which a windowless STA test cannot produce (review r1 F4).
    [StaFact]
    public void TryGetScreenRect_ReturnsFalse_ForAnUnloadedElement()
    {
        var btn = new Button { Width = 80, Height = 24 }; // detached: layout never ran
        var adapter = new WpfElementAdapter("t-rect", btn);

        var ok = adapter.TryGetScreenRect(out var rect);

        Check.That(ok).IsFalse();
        Check.That(rect).IsEqualTo(default(SdkRect));
    }

    // Activate's priority chain, branch 1 (DwellMode.Focus): focus the element and return — the
    // UIA Invoke provider must NOT run for a Focus-mode element.
    [StaFact]
    public void Activate_InFocusMode_SetsIsFocused_WithoutInvoking()
    {
        var btn = new Button();
        Dwell.SetMode(btn, DwellMode.Focus);
        var adapter = new WpfElementAdapter("t-focus", btn);
        var clicks = 0;
        btn.Click += (_, _) => clicks++;

        adapter.Activate();
        Pump();

        Check.That(btn.IsFocused).IsTrue();
        Check.That(clicks).IsEqualTo(0); // Focus mode must not fall through to UIA Invoke
    }

    // Activate's priority chain, branch 2 (DwellMode.Invoke): the UIA InvokeProvider.Invoke()
    // raises Click — WPF's ButtonBase.OnClick then runs the attached command as part of that ONE
    // click, so the command executes exactly once. A mutant that skips the UIA branch for the
    // ButtonBase command fallback (direct Command.Execute) never raises Click; a mutant that runs
    // the fallback IN ADDITION to UIA Invoke executes the command twice. Both are killed here.
    [StaFact]
    public void Activate_InInvokeMode_RaisesClickViaTheInvokeProvider_AndTheCommandRunsExactlyOnce()
    {
        var btn = new Button();
        var command = new RecordingCommand();
        btn.Command = command;
        Dwell.SetMode(btn, DwellMode.Invoke);
        var adapter = new WpfElementAdapter("t-invoke", btn);
        var clicks = 0;
        btn.Click += (_, _) => clicks++;

        adapter.Activate();
        Pump();

        Check.That(clicks).IsEqualTo(1);
        Check.That(command.ExecuteCount).IsEqualTo(1);
    }

    // WindowHandle (WpfElementAdapter.cs): an element not in a Window yields 0 on every read —
    // only a NON-zero handle is cached (:22), so each read recomputes and a mutant that caches the
    // zero is not distinguishable here. An integrator app that reads it before layout must not
    // get a bogus handle (review r1 F10: the earlier "cached second read" phrasing was wrong).
    [StaFact]
    public void WindowHandle_IsZero_ForAnElementNotInAWindow()
    {
        var btn = new Button(); // never placed in a Window
        var adapter = new WpfElementAdapter("t-hwnd", btn);

        Check.That(adapter.WindowHandle).IsEqualTo(0L);
        Check.That(adapter.WindowHandle).IsEqualTo(0L); // second read recomputes: zero is never cached
    }
}
