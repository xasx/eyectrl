namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;

/// <summary>
/// Owns the keyboard-input / interaction-action surface of <see cref="EventCoordinator"/>: key
/// activation, prediction selection, special-key, and interaction-action (click/scroll/note)
/// message handlers. Extracted from the former <c>InputActionDispatcher</c> as part of the
/// sub-coordinator decomposition (the input-action surface was split along the
/// quick-actions-vs-keyboard/interaction seam — the two halves share no state and make no
/// cross-calls). Methods are <see langword="internal"/> so the migrated tests call them directly
/// (no reflection).
/// </summary>
internal sealed class KeyboardInteractionDispatcher : CoordinatorBase
{
    private const int DefaultScrollAmount = 3;
    private const int BackspaceVirtualKeyCode = 0x08;
    private const int EnterVirtualKeyCode = 0x0D;
    private const int TabVirtualKeyCode = 0x09;
    private const ushort SpaceVirtualKeyCode = 0x20;

    protected override void OnInitialize()
    {
        var context = Context!;
        var m = context.Messenger;
        m.Register<KeyboardInteractionDispatcher, KeyActivatedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleKeyActivation(msg.Key, msg.Modifiers)));
        m.Register<KeyboardInteractionDispatcher, PredictionSelectedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandlePredictionSelected(msg.DeleteCount, msg.Word)));
        m.Register<KeyboardInteractionDispatcher, SpecialKeyMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleSpecialKey(msg.KeyName, msg.Modifiers)));
        m.Register<KeyboardInteractionDispatcher, ActionRequestedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleActionRequested(msg)));
    }

    internal void HandleKeyActivation(string key, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (Context is null) return;

        var hasSystemModifiers = modifiers != null &&
            (modifiers.Contains(ModifierKey.Ctrl) || modifiers.Contains(ModifierKey.Alt) || modifiers.Contains(ModifierKey.Win));
        var hasShift = modifiers != null && modifiers.Contains(ModifierKey.Shift);
        var isControlKey = key is "\b" or "\n" or "\t" or " ";

        // Use VK code path when system modifiers are active, or Shift + control keys (e.g., Shift+Tab)
        if (hasSystemModifiers || (hasShift && isControlKey))
        {
            // VK code path: wrap keystroke in modifier down/up
            ushort vk;
            IReadOnlyList<ModifierKey> requiredModifiers = Array.Empty<ModifierKey>();

            switch (key)
            {
                case "\b": vk = (ushort)BackspaceVirtualKeyCode; break;
                case "\n": vk = (ushort)EnterVirtualKeyCode; break;
                case "\t": vk = (ushort)TabVirtualKeyCode; break;
                case " ": vk = SpaceVirtualKeyCode; break;
                default:
                    if (key.Length == 1)
                    {
                        // Use lowercase for letters so CapsLock-induced uppercase
                        // doesn't trick VkKeyScanW into reporting Shift as required
                        var ch = char.IsLetter(key[0]) ? char.ToLowerInvariant(key[0]) : key[0];
                        (vk, requiredModifiers) = Win32InputSimulator.CharToVirtualKey(ch);
                    }
                    else
                        vk = 0;
                    break;
            }

            if (vk != 0)
            {
                // Merge any modifiers the character intrinsically requires
                // (e.g., '!' needs Shift, AltGr chars need Ctrl+Alt)
                if (requiredModifiers.Count > 0)
                {
                    var merged = new HashSet<ModifierKey>(modifiers!);
                    foreach (var req in requiredModifiers)
                        merged.Add(req);
                    Context.InputSimulator.SendModifiedKeyPress(merged, vk);
                }
                else
                {
                    Context.InputSimulator.SendModifiedKeyPress(modifiers!, vk);
                }
            }
            else
            {
                // Character has no VK mapping (e.g., €, ©) — fall back to plain text
                Log.Warning("No VK mapping for key '{Key}' with system modifiers, falling back to plain text", key);
                Context.InputSimulator.SendText(key);
            }
        }
        else
        {
            // Simple path: no system modifiers, just send text or VK
            switch (key)
            {
                case "\b": Context.InputSimulator.SendKeyPress(BackspaceVirtualKeyCode); break;
                case "\n": Context.InputSimulator.SendKeyPress(EnterVirtualKeyCode); break;
                case "\t": Context.InputSimulator.SendKeyPress(TabVirtualKeyCode); break;
                default: Context.InputSimulator.SendText(key); break;
            }
        }
    }

    internal void HandlePredictionSelected(int deleteCount, string word)
    {
        if (Context is null) return;

        for (int i = 0; i < deleteCount; i++)
            Context.InputSimulator.SendKeyPress(BackspaceVirtualKeyCode);
        Context.InputSimulator.SendText(word + " ");
    }

    internal void HandleSpecialKey(string keyName, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (Context is null) return;

        // KeyNameMapper is the single name→VK authority (shared with the QuickAction chord
        // parser and the keyboard's "shortcut:" keys). It also covers the text-control keys
        // (Backspace/Enter/Tab/Space) that the inline switch this replaced omitted, which
        // modified special-key sends now need.
        if (KeyNameMapper.TryMap(keyName, out var vk, out var isExtendedKey))
        {
            if (modifiers != null && modifiers.Count > 0)
                Context.InputSimulator.SendModifiedKeyPress(modifiers, vk, isExtendedKey);
            else
                Context.InputSimulator.SendKeyPress(vk, isExtendedKey);
        }
        else
        {
            Log.Warning("Unknown special key name: {KeyName}", keyName);
        }
    }

    internal void HandleActionRequested(ActionRequestedMessage msg)
    {
        if (Context is null) return;

        var pt = msg.IsPreciseTarget ? msg.Point : (Context.GazeEngine.StableCentroid ?? msg.Point);
        Log.Information("Action: {Action} at ({X:F0}, {Y:F0}) [PreciseTarget={IsPreciseTarget}] [Modifiers={Modifiers}]",
            msg.Action, pt.X, pt.Y, msg.IsPreciseTarget, msg.Modifiers);

        if (msg.Action == InteractionAction.PinNote)
        {
            if (Context is { NoteManager.IsAtCapacity: false })
                Context.NoteManager.CreateNoteAt(pt);

            if (Context is { TargetHighlightVM.IsVisible: true })
            {
                Context.TargetHighlightVM.IsVisible = false;
                Context.OverlayManager.TargetHighlight.HideHighlight();
            }
            return;
        }

        // The ring's keyboard action: no click point, no input simulation — just the show
        // message. Falls through to the shared target-highlight cleanup below (unlike PinNote,
        // which duplicates it before its return).
        if (msg.Action == InteractionAction.Keyboard)
            Context.Messenger.Send(new KeyboardShowRequestedMessage());

        // Modifier routing: a non-empty set goes to the modified (atomic-batch) overloads; null
        // AND empty sets stay on the bare methods — byte-identical to the pre-modifier paths.
        // Modified scrolls send exactly one notch (the batch's leading move already puts the wheel
        // on the point; the arm's sign still carries the direction).
        var mods = msg.Modifiers;
        var modified = mods is { Count: > 0 };

        switch (msg.Action)
        {
            case InteractionAction.LeftClick:
                if (modified) Context.InputSimulator.LeftClick(pt, mods); else Context.InputSimulator.LeftClick(pt);
                break;
            case InteractionAction.RightClick:
                if (modified) Context.InputSimulator.RightClick(pt, mods); else Context.InputSimulator.RightClick(pt);
                break;
            case InteractionAction.DoubleClick:
                if (modified) Context.InputSimulator.DoubleClick(pt, mods); else Context.InputSimulator.DoubleClick(pt);
                break;
            case InteractionAction.MiddleClick:
                if (modified) Context.InputSimulator.MiddleClick(pt, mods); else Context.InputSimulator.MiddleClick(pt);
                break;

            case InteractionAction.ScrollUp:
                if (modified) Context.InputSimulator.Scroll(pt, 0, 1, mods); else Context.InputSimulator.Scroll(pt, 0, DefaultScrollAmount);
                break;
            case InteractionAction.ScrollDown:
                if (modified) Context.InputSimulator.Scroll(pt, 0, -1, mods); else Context.InputSimulator.Scroll(pt, 0, -DefaultScrollAmount);
                break;
            case InteractionAction.ScrollLeft:
                if (modified) Context.InputSimulator.Scroll(pt, -1, 0, mods); else Context.InputSimulator.Scroll(pt, -DefaultScrollAmount, 0);
                break;
            case InteractionAction.ScrollRight:
                if (modified) Context.InputSimulator.Scroll(pt, 1, 0, mods); else Context.InputSimulator.Scroll(pt, DefaultScrollAmount, 0);
                break;
        }

        if (Context.TargetHighlightVM.IsVisible)
        {
            Context.TargetHighlightVM.IsVisible = false;
            Context.OverlayManager.TargetHighlight.HideHighlight();
        }
    }

}
