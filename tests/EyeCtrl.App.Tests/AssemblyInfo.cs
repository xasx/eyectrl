using EyeCtrl.Tests.TestSupport;

// Resets DpiHelper's process-global scale after every test in this assembly. It is not optional:
// five tests in DisplayGeometryWatcherTests drove production code that writes 2.0 and left it
// there, and the emoji dwell probe then activated the category tab instead of the cell under gaze.
// The obligation is pinned (not described) by
// DpiHelperWriterIsolationTests.TheRestoreHook_IsInstalledOnThisAssembly and
// EveryWriterBearingProject_InstallsBothScaleGuards. Read the attribute's own summary before
// removing or weakening either.
[assembly: DpiHelperRestore]

// The effect half: reddens the first test that STARTS on a non-1.0 scale, which is what a restore
// that stopped working looks like from the outside. Presence of that observer is pinned by
// DpiHelperWriterIsolationTests.TheScaleObserver_IsInstalledOnThisAssembly; its own summary says
// what the suite assumes of constructors and why the top layer stays documented.
[assembly: DpiHelperScaleObserver]

// Puts StringTable's process-global table back to embedded English after every test, and the
// observer reddens the first test that STARTS on a non-English global. Presence of both is pinned
// by StringTableIsolationTests in this assembly; German-path tests use injected tables or the
// "StringTable Tests (App)" exclusive collection.
[assembly: StringTableRestore]
[assembly: StringTableObserver]

// The RUNTIME half of the no-visible-window guard: fails the test after which any window of this process became really
// visible (visible, not cloaked, on a monitor, not prepared by InvisibleTestWindow) — including windows that PRODUCTION
// code opened, which the static scan (NoVisibleTestWindowGuardTests) cannot see. See RuntimeWindowMonitor; presence is
// pinned by RuntimeWindowGuardTests.TheCheck_IsInstalledOnThisAssembly.
[assembly: EyeCtrl.App.Tests.Fixtures.RuntimeVisibleWindowCheck]
