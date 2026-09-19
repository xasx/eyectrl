using EyeCtrl.Tests.TestSupport;

// Resets DpiHelper's process-global scale after every test in this assembly. ActionMenuLayoutHelperTests
// currently restores in Dispose as well; this hook does not replace that class's own cleanup, it makes
// the guarantee assembly-wide so a future writer cannot forget it. See DpiHelperRestoreAttribute, and
// DpiHelperWriterIsolationTests in EyeCtrl.App.Tests, which requires this declaration for every test
// project holding a DpiHelper writer.
[assembly: DpiHelperRestore]
