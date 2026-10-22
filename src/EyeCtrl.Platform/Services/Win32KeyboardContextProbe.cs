namespace EyeCtrl.Platform.Services;

using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Platform.Input;

/// <summary>
/// Win32 / UI Automation implementation of <see cref="IKeyboardContextProbe"/> — the two OS
/// facts E1's log reads. Content-free by construction: the foreground read returns a PROCESS
/// name and never touches the window title, and the text read returns characters only to a
/// caller that compares them in memory and logs lengths.
/// </summary>
public sealed class Win32KeyboardContextProbe : IKeyboardContextProbe
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    /// <inheritdoc />
    public string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception)
        {
            return null; // the process exited, access denied, anything: unknown
        }
    }

    /// <inheritdoc />
    public string? ReadFocusedTextTail(int maxChars)
    {
        if (maxChars <= 0) return null;
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return null;
            // Never read EyeCtrl's own UI, and never a password field, whatever the caller checked.
            if (focused.Current.ProcessId == OwnProcessId || focused.Current.IsPassword) return null;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
                || pattern is not TextPattern text)
                return null;

            var selection = text.GetSelection();
            if (selection.Length == 0) return null;

            // The caret is the selection's start; take the characters BEFORE it.
            var range = selection[0].Clone();
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -maxChars);
            return range.GetText(maxChars);
        }
        catch (Exception)
        {
            return null; // no pattern, element gone, COM fault: unsupported
        }
    }
}
