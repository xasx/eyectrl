namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;

/// <summary>
/// Owns the input-action surface of <see cref="EventCoordinator"/>: keyboard-chord / media / window
/// quick-action execution, plus the keyboard-input message handlers (key activation, prediction,
/// special key, action requested, task-switcher selection, quick-action selection). Extracted from
/// <c>EventCoordinator</c> as part of the sub-coordinator decomposition. Methods are
/// <see langword="internal"/> so the migrated tests call them directly (no reflection).
/// </summary>
internal sealed class InputActionDispatcher : IDisposable
{
    private EventCoordinationContext? _context;
    private const int DefaultScrollAmount = 3;
    private const int BackspaceVirtualKeyCode = 0x08;
    private const int EnterVirtualKeyCode = 0x0D;
    private const int TabVirtualKeyCode = 0x09;
    private const ushort SpaceVirtualKeyCode = 0x20;

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        var m = context.Messenger;
        m.Register<InputActionDispatcher, KeyActivatedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleKeyActivation(msg.Key, msg.Modifiers)));
        m.Register<InputActionDispatcher, PredictionSelectedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandlePredictionSelected(msg.DeleteCount, msg.Word)));
        m.Register<InputActionDispatcher, SpecialKeyMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleSpecialKey(msg.KeyName, msg.Modifiers)));
        m.Register<InputActionDispatcher, ActionRequestedMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleActionRequested(msg)));
        m.Register<InputActionDispatcher, QuickActionSelectionMessage>(this, (_, msg) =>
        {
            IntPtr? hwnd = msg.Action.Type == QuickActionType.WindowAction
                ? context.WindowManager.TryGetEligibleForegroundWindow() : null;
            context.Dispatcher.BeginInvoke(() => ExecuteQuickAction(msg.Action, hwnd));
        });
        m.Register<InputActionDispatcher, TaskSwitcherSelectionMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleTaskSwitcherSelection(msg)));
    }

    internal void HandleTaskSwitcherSelection(TaskSwitcherSelectionMessage msg)
    {
        if (_context == null) return;
        _context.WindowManager.BringToFront(msg.Hwnd);
        // HideOverlay is public on the VM so the coordinator can dismiss after a selection
        // (in addition to the toggle path inside Receive).
        _context.OverlayManager.TaskSwitcher.HideOverlay();
    }

    /// <summary>
    /// Executes the user-selected Quick Action, dispatching by <see cref="QuickActionType"/>.
    /// Errors are caught and logged so a failing action never tears down the gaze thread.
    /// </summary>
    internal void ExecuteQuickAction(QuickAction action, IntPtr? windowActionHwnd)
    {
        if (_context == null) return;
        try
        {
            switch (action.Type)
            {
                case QuickActionType.KeyboardShortcut:
                    ExecuteKeyboardShortcut(action.Payload);
                    break;
                case QuickActionType.CannedText:
                    _context.InputSimulator.SendText(action.Payload);
                    break;
                case QuickActionType.LaunchExe:
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(action.Payload) { UseShellExecute = true });
                    break;
                case QuickActionType.LaunchWindowsApp:
                    _context.WindowsAppLauncher.ActivateApplication(action.Payload);
                    break;
                case QuickActionType.LaunchUrl:
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(action.Payload) { UseShellExecute = true });
                    break;
                case QuickActionType.MediaControl:
                    ExecuteMediaControl(action.Payload);
                    break;
                case QuickActionType.WindowAction:
                    ExecuteWindowAction(action.Payload, windowActionHwnd);
                    break;
                default:
                    Log.Warning("Unknown QuickActionType: {Type}", action.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "QuickAction execution failed: {Type} '{Label}'", action.Type, action.Label);
        }
    }

    /// <summary>
    /// Parses a keyboard chord (e.g. "Ctrl+Shift+Enter") and sends it as a modified key press.
    /// Single-character keys are resolved via <see cref="Win32InputSimulator.CharToVirtualKey"/>;
    /// named keys (e.g. "Enter", "F5", "Left") via <see cref="KeyNameMapper.TryMap"/>.
    /// </summary>
    internal void ExecuteKeyboardShortcut(string chord)
    {
        if (_context == null) return;
        var parts = chord.Split('+');
        var mods = new HashSet<ModifierKey>();
        string key = parts[^1].Trim();   // trim whitespace so "Ctrl + C" works (modifiers are trimmed below)

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var mod = parts[i].Trim() switch
            {
                "Ctrl" => (ModifierKey?)ModifierKey.Ctrl,
                "Shift" => ModifierKey.Shift,
                "Alt" => ModifierKey.Alt,
                "Win" => ModifierKey.Win,
                _ => null   // unknown modifier → drop
            };
            if (mod.HasValue) mods.Add(mod.Value);
        }

        ushort vk = 0;
        bool isExtended = false;
        IReadOnlyList<ModifierKey> requiredModifiers = Array.Empty<ModifierKey>();
        if (key.Length == 1)
        {
            var ch = char.IsLetter(key[0]) ? char.ToLowerInvariant(key[0]) : key[0];
            (vk, requiredModifiers) = Win32InputSimulator.CharToVirtualKey(ch);
        }

        if (vk == 0 && KeyNameMapper.TryMap(key, out var mappedVk, out var mappedExtended))
        {
            vk = mappedVk;
            isExtended = mappedExtended;
        }

        if (vk != 0)
        {
            if (requiredModifiers.Count == 0)
            {
                _context.InputSimulator.SendModifiedKeyPress(mods, vk, isExtendedKey: isExtended);
                return;
            }

            var merged = new HashSet<ModifierKey>(mods);
            foreach (var requiredModifier in requiredModifiers)
                merged.Add(requiredModifier);

            _context.InputSimulator.SendModifiedKeyPress(merged, vk, isExtendedKey: isExtended);
        }
        else
        {
            Log.Warning("QuickAction: could not map keyboard chord '{Chord}'", chord);
        }
    }

    /// <summary>
    /// Sends a media-transport virtual-key press for the given command name.
    /// Media keys are extended keys (KEYEVENTF_EXTENDEDKEY).
    /// </summary>
    internal void ExecuteMediaControl(string command)
    {
        if (_context == null) return;
        ushort vk = command switch
        {
            "PlayPause" => 0xB3, "Stop" => 0xB2,
            "NextTrack" => 0xB0, "PreviousTrack" => 0xB1,
            "VolumeUp" => 0xAF, "VolumeDown" => 0xAE, "VolumeMute" => 0xAD,
            _ => 0
        };
        if (vk != 0)
            _context.InputSimulator.SendKeyPress(vk, isExtendedKey: true);
    }

    /// <summary>
    /// Applies a window-management quick action to <paramref name="hwnd"/>. The HWND is resolved by
    /// the <see cref="QuickActionSelectionMessage"/> recipient synchronously on the dwell-completion
    /// tick and passed in here — this method must NEVER re-query the foreground, or a foreground
    /// change during the dispatcher hop could redirect Close to a different window (B2 spec §4
    /// BLOCKER A.1). Mirrors how TaskSwitcherSelectionMessage carries its HWND.
    /// </summary>
    /// <param name="payload">The <see cref="WindowAction"/> enum member name from the stored action.</param>
    /// <param name="hwnd">The eligible foreground window, or null if none was eligible.</param>
    /// <remarks>
    /// Runs inside <c>ExecuteQuickAction</c>'s try/catch; a caller from anywhere else would need its own guard.
    /// </remarks>
    internal void ExecuteWindowAction(string payload, IntPtr? hwnd)
    {
        if (_context == null) return;

        // Enum.TryParse also accepts numeric strings, so a corrupt payload like "7" would parse into
        // an undefined WindowAction, fall through the switch and no-op silently. IsDefined keeps the
        // single diagnostic below as the only exit for anything unroutable.
        if (!Enum.TryParse(payload, out WindowAction windowAction) || !Enum.IsDefined(windowAction))
        {
            Log.Warning("QuickAction: unknown window action payload '{Payload}'", payload);
            return;
        }

        if (hwnd is null)
        {
            // Foreground was the shell, this process, zero, or an invalid/destroyed window.
            Log.Information("Window-action {Action} skipped: no eligible foreground window", windowAction);
            return;
        }

        switch (windowAction)
        {
            case WindowAction.Minimize:
                _context.WindowManager.Minimize(hwnd.Value);
                break;
            case WindowAction.MaximizeRestore:
                _context.WindowManager.MaximizeOrRestore(hwnd.Value);
                break;
            case WindowAction.Close:
                if (!_context.WindowManager.CloseWindow(hwnd.Value))
                    Log.Information("Window-action Close: PostMessage(WM_CLOSE) returned false (UIPI-blocked by elevated foreground?)");
                break;
        }
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

    public void Dispose() => _context?.Messenger.UnregisterAll(this);
}
