using EyeCtrl.Tests.TestSupport;

// Resets DpiHelper's process-global scale after every test in this assembly. CoordinateTypeTests
// and DpiHelperTests both set a non-1.0 scale and neither restored it, so later tests in this
// assembly inherited whatever the previous one left. Same hook, same reasoning, as EyeCtrl.App.Tests:
// see DpiHelperRestoreAttribute, and DpiHelperWriterIsolationTests in EyeCtrl.App.Tests, which
// requires this declaration for every test project holding a DpiHelper writer.
[assembly: DpiHelperRestore]
