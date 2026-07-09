namespace OffScreenGazeProbe;

using System.Windows;
/// <summary>
/// Standalone diagnostic probe. Classifies each raw Tobii gaze sample against the physical
/// screen bounds to answer: does the Stream Engine emit VALID coordinates past the screen edges?
///
/// This settles the blocking open question in the toolbar auto-hide design
/// (docs/superpowers/specs/2026-07-09-toolbar-auto-hide-design.md, Open question #1): the
/// directional "look past the docked edge" summon gesture only works if valid samples arrive
/// with out-of-range coordinates. If the tracker instead loses the eyes (invalid samples)
/// when looking past the bezel, the summon predicate must fall back to an edge-band-inside rule.
///
/// NOT added to EyeCtrl.slnx — build explicitly:
///   dotnet run --project samples/OffScreenGazeProbe
/// </summary>
public partial class App : Application
{
}
