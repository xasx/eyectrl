using EyeCtrl.Tests.TestSupport;

// Resets DpiHelper's process-global scale after every test in this assembly. It is not optional:
// five tests in DisplayGeometryWatcherTests drove production code that writes 2.0 and left it
// there, and the emoji dwell probe then activated the category tab instead of the cell under gaze.
// The obligation is pinned (not described) by
// DpiHelperWriterIsolationTests.TheRestoreHook_IsInstalledOnThisAssembly and
// EveryWriterBearingProject_InstallsTheRestoreHook. Read the attribute's own summary before
// removing or weakening either.
[assembly: DpiHelperRestore]
