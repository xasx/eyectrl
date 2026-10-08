using EyeCtrl.Tests.TestSupport;

// Puts StringTable's process-global table back to embedded English after every test, and the
// observer reddens the first test that STARTS on a non-English global. The Supervisor resolves its
// own tables, and its tests read them, so a leaked German global would change what later tests see.
// See StringTableIsolationTests in EyeCtrl.App.Tests.
[assembly: StringTableRestore]
[assembly: StringTableObserver]
