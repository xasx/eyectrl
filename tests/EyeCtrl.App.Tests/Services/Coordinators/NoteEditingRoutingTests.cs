namespace EyeCtrl.App.Tests.Services.Coordinators;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The P1 D1 routing guarantee, asserted end-to-end through the REAL dispatchers and the REAL
/// NoteManager: while a note is in Edit, a recording <see cref="IInputSimulator"/> receives ZERO
/// calls from the key, prediction, special-key, canned-text and shortcut paths — every output
/// lands in the note's draft or is swallowed. With no note editing, the pre-note behaviour is
/// byte-identical (the same handlers hit the simulator as before).
/// </summary>
public class NoteEditingRoutingTests
{
    [Fact]
    public void KeyActivation_WhileEditing_ReceivesZeroSimulatorCalls_AndLandsInTheDraft()
    {
        var (dispatcher, manager, input) = BuildEditing();

        dispatcher.HandleKeyActivation("a", null);
        dispatcher.HandleKeyActivation("b", null);
        dispatcher.HandleKeyActivation(" ", null);
        dispatcher.HandleKeyActivation("\b", null);
        dispatcher.HandleKeyActivation("\n", null);

        Check.WithCustomMessage("while a note is editing, no key may become OS input")
            .That(input.TotalCalls).IsEqualTo(0);
        // a, b, space, backspace, newline → "ab\n"
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("ab\n");
    }

    [Fact]
    public void Prediction_WhileEditing_ReceivesZeroSimulatorCalls_AndAppliesAtomically()
    {
        var (dispatcher, manager, input) = BuildEditing();
        dispatcher.HandleKeyActivation("h", null);
        dispatcher.HandleKeyActivation("i", null);

        dispatcher.HandlePredictionSelected(deleteCount: 2, word: "hi");

        Check.That(input.TotalCalls).IsEqualTo(0);
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("hi ");
    }

    [Fact]
    public void SpecialKeys_WhileEditing_ReceivesZeroSimulatorCalls()
    {
        var (dispatcher, manager, input) = BuildEditing();

        dispatcher.HandleSpecialKey("backspace", null);
        dispatcher.HandleSpecialKey("enter", null);
        dispatcher.HandleSpecialKey("space", null);

        Check.That(input.TotalCalls).IsEqualTo(0);
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("\n ");
    }

    [Fact]
    public void ChordsAndSwallowedKeys_WhileEditing_NeverReachTheSimulator()
    {
        var (dispatcher, manager, input) = BuildEditing();

        dispatcher.HandleKeyActivation("\t", null);                                   // tab
        dispatcher.HandleKeyActivation("c", Mods(ModifierKey.Ctrl));                  // chord
        dispatcher.HandleKeyActivation("\b", Mods(ModifierKey.Ctrl, ModifierKey.Alt)); // not deleteWord
        dispatcher.HandleSpecialKey("Escape", null);
        dispatcher.HandleSpecialKey("F5", null);

        Check.That(input.TotalCalls).IsEqualTo(0);
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("");
    }

    [Fact]
    public void DeleteWordChord_WhileEditing_TrimsTheNotesLastWord()
    {
        var (dispatcher, manager, input) = BuildEditing();
        dispatcher.HandleKeyActivation("h", null);
        dispatcher.HandleKeyActivation("i", null);
        dispatcher.HandleKeyActivation(" ", null);

        dispatcher.HandleKeyActivation("\b", Mods(ModifierKey.Ctrl));

        Check.That(input.TotalCalls).IsEqualTo(0);
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("");
    }

    [Fact]
    public void QuickActions_WhileEditing_CannedTextAppends_ShortcutAndMediaSwallowed()
    {
        var (_, manager, input) = BuildEditing();
        var dispatcher = BuildQuickActionDispatcher(input, manager);

        dispatcher.ExecuteQuickAction(new QuickAction { Type = QuickActionType.CannedText, Label = "hi", Payload = "hello" }, null);
        dispatcher.ExecuteQuickAction(new QuickAction { Type = QuickActionType.KeyboardShortcut, Label = "copy", Payload = "Ctrl+C" }, null);
        dispatcher.ExecuteQuickAction(new QuickAction { Type = QuickActionType.MediaControl, Label = "play", Payload = "PlayPause" }, null);

        Check.WithCustomMessage("while a note is editing, canned text, shortcuts and media keys may not become OS input")
            .That(input.TotalCalls).IsEqualTo(0);
        Check.That(manager.ActiveNotes[0].Draft).IsEqualTo("hello");
    }

    // ── with no note editing, the pre-note behaviour is untouched ──

    [Fact]
    public void NoNoteEditing_KeysStillReachTheSimulator()
    {
        var (_, manager, input) = BuildNotEditing();
        var keyboardDispatcher = BuildKeyboardDispatcher(input, manager);
        var quickDispatcher = BuildQuickActionDispatcher(input, manager);

        keyboardDispatcher.HandleKeyActivation("a", null);
        keyboardDispatcher.HandleKeyActivation("\b", null);
        keyboardDispatcher.HandleSpecialKey("Backspace", null);
        quickDispatcher.ExecuteQuickAction(new QuickAction { Type = QuickActionType.CannedText, Label = "hi", Payload = "hello" }, null);

        Check.That(input.TotalCalls).IsStrictlyGreaterThan(0);
        Check.That(input.SentTexts).Contains("a");
        Check.That(manager.ActiveNotes).IsEmpty();
    }

    [Fact]
    public void NullNoteManager_KeepsThePreNoteBehaviourByteIdentical()
    {
        // The existing dispatcher tests build contexts with NoteManager: null! — the routing
        // guard must read that as not-editing, which is what keeps them passing unchanged.
        var input = new RecordingInputSimulator();
        var ctx = TestFakes.MakeContext(input) with { TargetHighlightVM = new TargetHighlightOverlayViewModel() };
        var manager = new KeyboardInteractionDispatcher();
        manager.Initialize(ctx);

        Check.ThatCode(() =>
        {
            manager.HandleKeyActivation("a", null);
            manager.HandleSpecialKey("Backspace", null);
        }).DoesNotThrow();
        Check.That(input.TotalCalls).IsStrictlyGreaterThan(0);
    }

    // ── harness ──

    private static (KeyboardInteractionDispatcher Dispatcher, NoteManager Manager, RecordingInputSimulator Input) BuildEditing()
    {
        var input = new RecordingInputSimulator();
        var ctx = TestFakes.MakeContext(input) with { TargetHighlightVM = new TargetHighlightOverlayViewModel() };
        // No `using`: the manager outlives this builder (windowless targets only, nothing to leak).
        var manager = new NoteManager(ctx.Messenger, hitTester: InactiveNoteHitTester.Instance);
        ctx = ctx with { NoteManager = manager };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);
        manager.AddEditingNoteTargetsForTest(new PhysicalPoint(0, 0));
        return (dispatcher, manager, input);
    }

    private static (KeyboardInteractionDispatcher Dispatcher, NoteManager Manager, RecordingInputSimulator Input) BuildNotEditing()
    {
        var input = new RecordingInputSimulator();
        var ctx = TestFakes.MakeContext(input) with { TargetHighlightVM = new TargetHighlightOverlayViewModel() };
        var manager = new NoteManager(ctx.Messenger, hitTester: InactiveNoteHitTester.Instance);
        ctx = ctx with { NoteManager = manager };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);
        return (dispatcher, manager, input);
    }

    private static QuickActionDispatcher BuildQuickActionDispatcher(RecordingInputSimulator input, NoteManager manager)
    {
        var ctx = TestFakes.MakeContext(input) with { TargetHighlightVM = new TargetHighlightOverlayViewModel(), NoteManager = manager };
        var dispatcher = new QuickActionDispatcher();
        dispatcher.Initialize(ctx);
        return dispatcher;
    }

    private static KeyboardInteractionDispatcher BuildKeyboardDispatcher(RecordingInputSimulator input, NoteManager manager)
    {
        var ctx = TestFakes.MakeContext(input) with { TargetHighlightVM = new TargetHighlightOverlayViewModel(), NoteManager = manager };
        var dispatcher = new KeyboardInteractionDispatcher();
        dispatcher.Initialize(ctx);
        return dispatcher;
    }

    private static IReadOnlySet<ModifierKey> Mods(params ModifierKey[] keys) => new HashSet<ModifierKey>(keys);

    /// <summary>Counts EVERY interface call, so "zero" is a claim about the whole surface, not a
    /// spot-check of one method (private nested fake, no mocking library).</summary>
    internal sealed class RecordingInputSimulator : IInputSimulator
    {
        public int TotalCalls { get; private set; }
        public List<string> SentTexts { get; } = new();

        private void Count() => TotalCalls++;

        public void MoveCursor(PhysicalPoint point) => Count();
        public void LeftClick(PhysicalPoint point) => Count();
        public void RightClick(PhysicalPoint point) => Count();
        public void DoubleClick(PhysicalPoint point) => Count();
        public void MiddleClick(PhysicalPoint point) => Count();
        public void MouseDown(PhysicalPoint point) => Count();
        public void MouseUp(PhysicalPoint point) => Count();
        public void Scroll(PhysicalPoint point, int deltaX, int deltaY) => Count();
        public void SendKeyPress(ushort virtualKeyCode) => Count();
        public void SendKeyDown(ushort virtualKeyCode) => Count();
        public void SendKeyUp(ushort virtualKeyCode) => Count();
        public void SendText(string text) { Count(); SentTexts.Add(text); }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) => Count();
        public void SendKeyPress(ushort virtualKeyCode, bool isExtendedKey) => Count();
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode, bool isExtendedKey) => Count();
    }
}
