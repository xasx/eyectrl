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

    public TaskSwitcherTileViewModel(IntPtr hwnd, string title, uint processId, string processName, PhysicalPoint position)
    {
        Hwnd = hwnd;
        Title = title;
        ProcessId = processId;
        ProcessName = processName;
        _position = position;
    }
}
