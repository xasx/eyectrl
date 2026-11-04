using EyeCtrl.Tests.TestSupport;

// Every test project that references EyeCtrl.Core installs both string-table guards: Core's
// StringTable is process-global, and a test that activates a German table would otherwise change
// what every later test in this assembly reads. The obligation is pinned by
// EyeCtrl.App.Tests' StringTableIsolationTests.EveryCoreReferencingTestProject_InstallsBothGuards.
[assembly: StringTableRestore]
[assembly: StringTableObserver]
