using System.Runtime.CompilerServices;
using EyeCtrl.App.Services;
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

/// <summary>
/// T6 fixup-08 R4: this assembly runs on the OWNER's machine, so no note test may put a note or
/// Place-here window on the screen. The note manager's show seam is switched off ONCE here - before
/// any test runs - so every manager this assembly builds skips only the HWND show while its drawn
/// tree, placement, target registrations and "displayed" state behave as if the window had been
/// shown (see <c>NoteManager.RealNoteWindows</c> and <c>NoteWindow.ShowForDisplay</c>).
/// <para>
/// A test that genuinely needs a real note HWND switches the seam back on for its own scope
/// (try { RealNoteWindows = true; } finally { RealNoteWindows = false; }) and sits behind
/// AppFixture.SkipIfDisabled(), so it never runs on the owner's default run. It must also live in
/// an EXCLUSIVE collection, because the flag is process-global - both tests that switch it on
/// (NoteGateTests.TheRealWindowFromPoint_... and NoteControlInputTests.Touch_OnTheGlyphLabel_...)
/// sit in the exclusive DpiHelper Tests (App) collection. The obligations are pinned by
/// NoteWindowShowSeamTests: the default run keeps the flag off, and every manager it builds is
/// headless.
/// </para>
/// </summary>
internal static class HeadlessNoteWindowDefaults
{
    [ModuleInitializer]
    internal static void Initialize() => NoteManager.RealNoteWindows = false;
}
