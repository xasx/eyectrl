namespace EyeCtrl.App.Tests.Services.Coordinators;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging;
using NFluent;
using Xunit;

/// <summary>
/// E1's "keyboard shown" / "keyboard hidden" lines, as the coordinator decides them
/// (<see cref="KeyboardCoordinator.RecordVisibilityE1"/>). The decision is the testable half: real
/// transitions only, the foreground PROCESS name on a show, the cause on a hide, and the mirror
/// check started BEFORE the hide. The call from <c>SetKeyboardVisible</c> touches the real window,
/// so it is pinned by source shape — the same accepted residual as the reload wiring in
/// KeyboardCoordinatorTests: a mutation that deletes the call survives headless runs.
/// </summary>
public class KeyboardVisibilityE1Tests
{
    private sealed class Capture : ILogger<KeyboardViewModel>
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _lines.Add(formatter(state, exception));
        }

        public string[] Lines() { lock (_gate) return _lines.ToArray(); }
    }

    private sealed class FakeProbe : IKeyboardContextProbe
    {
        public string? GetForegroundProcessName() => "WINWORD.exe";
        public string? ReadFocusedTextTail(int maxChars) => null;
    }

    private static (KeyboardViewModel Vm, Capture Log) NewVm()
    {
        var log = new Capture();
        var vm = new KeyboardViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger(),
            logger: log, contextProbe: new FakeProbe());
        return (vm, log);
    }

    [Fact]
    public void AShow_LogsTheForegroundProcessNameTheEdgeAndTheSource()
    {
        var (vm, log) = NewVm();

        KeyboardCoordinator.RecordVisibilityE1(vm, visible: true, wasVisible: false, "top", "toolbar-toggle");

        Check.That(log.Lines()).ContainsExactly("KB-E1 shown proc=WINWORD.exe edge=top via=toolbar-toggle");
    }

    [Fact]
    public async Task AHide_LogsItsCause_AndStartsTheMirrorCheckBeforeIt()
    {
        var (vm, log) = NewVm();
        vm.ActivateKey("a", null);

        KeyboardCoordinator.RecordVisibilityE1(vm, visible: false, wasVisible: true, "top", "edge-close");

        // The mirror verdict is logged off-thread (it came back "unsupported": the probe has no
        // text); the hidden line is logged synchronously, after the check was started.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (log.Lines().Length < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        var lines = log.Lines();
        Check.That(lines).Contains("KB-E1 hidden cause=edge-close visiblems=-1");
        Check.That(lines.Any(l => l.StartsWith("KB-E1 mirror at=hide ", StringComparison.Ordinal))).IsTrue();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ARepeatedShowOrHide_IsNotAnEvent(bool visible, bool wasVisible)
    {
        var (vm, log) = NewVm();

        KeyboardCoordinator.RecordVisibilityE1(vm, visible, wasVisible, "top", "request");

        Check.That(log.Lines()).IsEmpty();
    }

    [Fact]
    public void WithoutAKeyboardViewModel_NothingHappens()
    {
        KeyboardCoordinator.RecordVisibilityE1(null, visible: true, wasVisible: false, "top", "ring");
        KeyboardCoordinator.RecordVisibilityE1(null, visible: false, wasVisible: true, "top", "ring");
    }

    [Fact]
    public void SetKeyboardVisible_RecordsTheTransition_BeforeTheWindowChanges()
    {
        // Source-shape pin (the call is unreachable headless; see the class remarks). The line must
        // sit right after the modal flag is applied and BEFORE Show()/Hide(): the hide's mirror
        // snapshot has to precede the hide, and the "was visible" flag has to be read before
        // ApplyModalKeyboardState overwrites it.
        var source = Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Services", "Coordinators",
                "KeyboardCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("SetKeyboardVisible no longer reads the previous visibility before applying "
                + "the new one, then records the transition before the window changes.")
            .That(source.Contains(
                "varwasVisible=_modal.KeyboardVisible;ApplyModalKeyboardState(visible);"
                + "Log.Information(\"Keyboard{State}\",visible?\"shown\":\"hidden\");"
                + "RecordVisibilityE1(Context.KeyboardVM,visible,wasVisible,"
                + "Context.Settings.Keyboard.ScreenEdge.ToLowerInvariant(),source);if(visible){")).IsTrue();
    }

    [Fact]
    public void TheFlipHandler_RecordsTheFlip_WithBothEdges()
    {
        var source = Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Services", "Coordinators",
                "KeyboardCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("HandleKeyboardEdgeToggled no longer records the Flip (before and after edge).")
            .That(source.Contains(
                "varedgeBefore=Context.Settings.Keyboard.ScreenEdge.ToLowerInvariant();"
                + "var(flipped,snapshot)=ApplyKeyboardEdge(Context.Settings,Context.SettingsService,_modelRegistry);"
                + "Context.KeyboardVM?.E1.RecordEdge(\"flip\",flipped.ToLowerInvariant(),edgeBefore);")).IsTrue();
    }
}
