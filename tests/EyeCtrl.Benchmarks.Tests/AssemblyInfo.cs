using EyeCtrl.Tests.TestSupport;

// Puts StringTable's process-global table back to embedded English after every test, and the
// observer reddens the first test that STARTS on a non-English global. Installed here because the
// meta-test's rule is every Core-referencing test project, not only the ones that touch the table
// today — a project that starts touching it cannot silently forget the guard.
// See StringTableIsolationTests in EyeCtrl.App.Tests.
[assembly: StringTableRestore]
[assembly: StringTableObserver]
