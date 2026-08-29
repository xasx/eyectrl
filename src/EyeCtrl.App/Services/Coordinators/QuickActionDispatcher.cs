namespace EyeCtrl.App.Services.Coordinators;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;

/// <summary>
/// Owns the quick-action surface of <see cref="EventCoordinator"/>: keyboard-chord / media /
/// window quick-action execution, plus the task-switcher selection and quick-action selection
/// message handlers. Extracted from the former <c>InputActionDispatcher</c> as part of the
/// sub-coordinator decomposition (the input-action surface was split along the
/// quick-actions-vs-keyboard/interaction seam — the two halves share no state and make no
/// cross-calls). Methods are <see langword="internal"/> so the migrated tests call them directly
/// (no reflection).
/// </summary>
internal sealed class QuickActionDispatcher : CoordinatorBase
{
    protected override void OnInitialize()
    {
        var context = Context!;
        var m = context.Messenger;
        m.Register<QuickActionDispatcher, QuickActionSelectionMessage>(this, (_, msg) =>
        {
            IntPtr? hwnd = msg.Action.Type == QuickActionType.WindowAction
                ? context.WindowManager.TryGetEligibleForegroundWindow() : null;
            context.Dispatcher.BeginInvoke(() => ExecuteQuickAction(msg.Action, hwnd));
        });
        m.Register<QuickActionDispatcher, TaskSwitcherSelectionMessage>(this, (_, msg) =>
            context.Dispatcher.BeginInvoke(() => HandleTaskSwitcherSelection(msg)));
    }

    internal void HandleTaskSwitcherSelection(TaskSwitcherSelectionMessage msg)
    {
        if (Context is null) return;
        Context.WindowManager.BringToFront(msg.Hwnd);
        // HideOverlay is public on the VM so the coordinator can dismiss after a selection
        // (in addition to the toggle path inside Receive).
        Context.OverlayManager.TaskSwitcher.HideOverlay();
    }

    /// <summary>
    /// Executes the user-selected Quick Action, dispatching by <see cref="QuickActionType"/>.
    /// Errors are caught and logged so a failing action never tears down the gaze thread.
    /// </summary>
    internal void ExecuteQuickAction(QuickAction action, IntPtr? windowActionHwnd)
    {
        if (Context is null) return;
        try
        {
            switch (action.Type)
            {
                case QuickActionType.KeyboardShortcut:
                    ExecuteKeyboardShortcut(action.Payload);
                    break;
                case QuickActionType.CannedText:
                    Context.InputSimulator.SendText(action.Payload);
                    break;
                case QuickActionType.LaunchExe:
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(action.Payload) { UseShellExecute = true });
                    break;
                case QuickActionType.LaunchWindowsApp:
                    Context.WindowsAppLauncher.ActivateApplication(action.Payload);
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
        if (Context is null) return;
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
                Context.InputSimulator.SendModifiedKeyPress(mods, vk, isExtendedKey: isExtended);
                return;
            }

            var merged = new HashSet<ModifierKey>(mods);
            foreach (var requiredModifier in requiredModifiers)
                merged.Add(requiredModifier);

            Context.InputSimulator.SendModifiedKeyPress(merged, vk, isExtendedKey: isExtended);
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
        if (Context is null) return;
        ushort vk = command switch
        {
            "PlayPause" => 0xB3, "Stop" => 0xB2,
            "NextTrack" => 0xB0, "PreviousTrack" => 0xB1,
            "VolumeUp" => 0xAF, "VolumeDown" => 0xAE, "VolumeMute" => 0xAD,
            _ => 0
        };
        if (vk != 0)
            Context.InputSimulator.SendKeyPress(vk, isExtendedKey: true);
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
        if (Context is null) return;

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
                Context.WindowManager.Minimize(hwnd.Value);
                break;
            case WindowAction.MaximizeRestore:
                Context.WindowManager.MaximizeOrRestore(hwnd.Value);
                break;
            case WindowAction.Close:
                if (!Context.WindowManager.CloseWindow(hwnd.Value))
                    Log.Information("Window-action Close: PostMessage(WM_CLOSE) returned false (UIPI-blocked by elevated foreground?)");
                break;
        }
    }

}
