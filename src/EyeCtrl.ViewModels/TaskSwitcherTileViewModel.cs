namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Models;

/// <summary>
/// One tile in the Task Switcher overlay, representing a single open window.
/// WPF-free (ViewModels targets plain net10.0): the icon is stored as a boxed
/// <c>object?</c> and resolved by the View (<c>TaskSwitcherWindow</c>) via
/// <c>IAppIconProvider</c> using <see cref="ProcessId"/>. WPF's Image.Source
/// binding accepts any object that is an ImageSource at runtime.
/// </summary>
public partial class TaskSwitcherTileViewModel : ObservableObject
{
    /// <summary>Window handle (the dwell target id and the BringToFront argument).</summary>
    public IntPtr Hwnd { get; }

    /// <summary>Window title (from GetWindowText).</summary>
    public string Title { get; }

    /// <summary>Owning process ID — used by the View to resolve the icon.</summary>
    public uint ProcessId { get; }

    /// <summary>Owning process name (for diagnostics / future icon lookup strategies).</summary>
    public string ProcessName { get; }

    /// <summary>
    /// True when the window was iconic (<see cref="Core.Models.AppWindow.IsMinimized"/>) at
    /// enumeration: the View draws the "Minimiert" badge inside the tile face. Read-only — the
    /// snapshot is one-shot per session, and looking at the tile still restores and switches, so
    /// the tile's state must not be edited after it was built.
    /// </summary>
    public bool IsMinimized { get; }

    /// <summary>
    /// True for the switcher's "Show desktop" tile — the last tile of a session, which is not a
    /// window at all (its <see cref="Hwnd"/> is <see cref="IntPtr.Zero"/> and its title is empty).
    /// The View draws it outlined with its own glyph and label; the VM gives it
    /// <c>TaskSwitcherViewModel.DesktopTargetId</c> as a dwell target and minimises the session's
    /// windows when it fires. Display and hit-testing are otherwise identical to a window tile —
    /// it is a normal dwell target under every slice-1 rule.
    /// </summary>
    public bool IsDesktop { get; }

    /// <summary>
    /// Process icon as a boxed object. Set by the View (TaskSwitcherWindow) via
    /// IAppIconProvider after the tile is created. Typed as <c>object?</c> (not
    /// <c>ImageSource</c>) so this VM stays WPF-free (ViewModels targets plain net10.0).
    /// </summary>
    [ObservableProperty] private object? _icon;

    /// <summary>Screen center in physical pixels, set by the View after layout for dwell hit-testing.</summary>
    [ObservableProperty] private PhysicalPoint _position;

    /// <summary>Dwell progress 0.0–1.0, driven by the VM's DwellDetector via OnTargetProgress.</summary>
    [ObservableProperty] private double _dwellProgress;
    /// <summary>
    /// True while this item's dwell is HELD: the gaze stepped off mid-dwell and <see cref="DwellProgress"/>
    /// is kept for the pause grace. The view dims the progress indicator; it never changes the value.
    /// Display-only — nothing reads it to decide whether a dwell holds.
    /// </summary>
    [ObservableProperty] private bool _isDwellPaused;

    public TaskSwitcherTileViewModel(
        IntPtr hwnd, string title, uint processId, string processName, PhysicalPoint position,
        bool isMinimized = false, bool isDesktop = false)
    {
        Hwnd = hwnd;
        Title = title;
        ProcessId = processId;
        ProcessName = processName;
        IsMinimized = isMinimized;
        IsDesktop = isDesktop;
        _position = position;
    }
}
