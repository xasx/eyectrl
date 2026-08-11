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
internal sealed class KeyboardInteractionDispatcher : IDisposable
{
    private EventCoordinationContext? _context;
    private bool _disposed;
    private const int DefaultScrollAmount = 3;
    private const int BackspaceVirtualKeyCode = 0x08;
    private const int EnterVirtualKeyCode = 0x0D;
    private const int TabVirtualKeyCode = 0x09;
    private const ushort SpaceVirtualKeyCode = 0x20;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
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
        if (_context == null) return;

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
                    _context.InputSimulator.SendModifiedKeyPress(merged, vk);
                }
                else
                {
                    _context.InputSimulator.SendModifiedKeyPress(modifiers!, vk);
                }
            }
            else
            {
                // Character has no VK mapping (e.g., €, ©) — fall back to plain text
                Log.Warning("No VK mapping for key '{Key}' with system modifiers, falling back to plain text", key);
                _context.InputSimulator.SendText(key);
            }
        }
        else
        {
            // Simple path: no system modifiers, just send text or VK
            switch (key)
            {
                case "\b": _context.InputSimulator.SendKeyPress(BackspaceVirtualKeyCode); break;
                case "\n": _context.InputSimulator.SendKeyPress(EnterVirtualKeyCode); break;
                case "\t": _context.InputSimulator.SendKeyPress(TabVirtualKeyCode); break;
                default: _context.InputSimulator.SendText(key); break;
            }
        }
    }

    internal void HandlePredictionSelected(int deleteCount, string word)
    {
        if (_context == null) return;

        for (int i = 0; i < deleteCount; i++)
            _context.InputSimulator.SendKeyPress(BackspaceVirtualKeyCode);
        _context.InputSimulator.SendText(word + " ");
    }

    internal void HandleSpecialKey(string keyName, IReadOnlySet<ModifierKey>? modifiers)
    {
        if (_context == null) return;

        ushort vk = keyName.ToLowerInvariant() switch
        {
            "escape" => VirtualKeys.Escape,
            "f1" => VirtualKeys.F1,
            "f2" => VirtualKeys.F2,
            "f3" => VirtualKeys.F3,
            "f4" => VirtualKeys.F4,
            "f5" => VirtualKeys.F5,
            "f6" => VirtualKeys.F6,
            "f7" => VirtualKeys.F7,
            "f8" => VirtualKeys.F8,
            "f9" => VirtualKeys.F9,
            "f10" => VirtualKeys.F10,
            "f11" => VirtualKeys.F11,
            "f12" => VirtualKeys.F12,
            "home" => VirtualKeys.Home,
            "end" => VirtualKeys.End,
            "pageup" => VirtualKeys.Prior,
            "pagedown" => VirtualKeys.Next,
            "arrowup" => VirtualKeys.Up,
            "arrowdown" => VirtualKeys.Down,
            "arrowleft" => VirtualKeys.Left,
            "arrowright" => VirtualKeys.Right,
            "insert" => VirtualKeys.Insert,
            "delete" => VirtualKeys.Delete,
            "printscreen" => VirtualKeys.Snapshot,
            _ => 0
        };

        if (vk != 0)
        {
            // Arrow keys, Insert/Delete, Home/End, PageUp/PageDown, and PrintScreen require KEYEVENTF_EXTENDEDKEY
            bool isExtendedKey = vk is VirtualKeys.Up or VirtualKeys.Down or VirtualKeys.Left or VirtualKeys.Right
                or VirtualKeys.Home or VirtualKeys.End or VirtualKeys.Prior or VirtualKeys.Next
                or VirtualKeys.Insert or VirtualKeys.Delete or VirtualKeys.Snapshot;

            if (modifiers != null && modifiers.Count > 0)
                _context.InputSimulator.SendModifiedKeyPress(modifiers, vk, isExtendedKey);
            else
                _context.InputSimulator.SendKeyPress(vk, isExtendedKey);
        }
        else
        {
            Log.Warning("Unknown special key name: {KeyName}", keyName);
        }
    }

    internal void HandleActionRequested(ActionRequestedMessage msg)
    {
        if (_context == null) return;

        var pt = msg.IsPreciseTarget ? msg.Point : (_context.GazeEngine.StableCentroid ?? msg.Point);
        Log.Information("Action: {Action} at ({X:F0}, {Y:F0}) [PreciseTarget={IsPreciseTarget}]",
            msg.Action, pt.X, pt.Y, msg.IsPreciseTarget);

        if (msg.Action == InteractionAction.PinNote)
        {
            if (_context is { NoteManager.IsAtCapacity: false })
                _context.NoteManager.CreateNoteAt(pt);

            if (_context is { TargetHighlightVM.IsVisible: true })
            {
                _context.TargetHighlightVM.IsVisible = false;
                _context.OverlayManager.TargetHighlight.HideHighlight();
            }
            return;
        }

        switch (msg.Action)
        {
            case InteractionAction.LeftClick:    _context.InputSimulator.LeftClick(pt);        break;
            case InteractionAction.RightClick:   _context.InputSimulator.RightClick(pt);       break;
            case InteractionAction.DoubleClick:  _context.InputSimulator.DoubleClick(pt);      break;

            case InteractionAction.ScrollUp:     _context.InputSimulator.Scroll(pt, 0, DefaultScrollAmount);    break;
            case InteractionAction.ScrollDown:   _context.InputSimulator.Scroll(pt, 0, -DefaultScrollAmount);   break;
            case InteractionAction.ScrollLeft:   _context.InputSimulator.Scroll(pt, -DefaultScrollAmount, 0);   break;
            case InteractionAction.ScrollRight:  _context.InputSimulator.Scroll(pt, DefaultScrollAmount, 0);    break;
        }

        if (_context.TargetHighlightVM.IsVisible)
        {
            _context.TargetHighlightVM.IsVisible = false;
            _context.OverlayManager.TargetHighlight.HideHighlight();
        }
    }

    public void Dispose()
    {
        if (_disposed || _context is null) { _disposed = true; return; }
        _context.Messenger.UnregisterAll(this);
        _disposed = true;
    }
}
