using EyeCtrl.Tests.TestSupport;

// Resets DpiHelper's process-global scale after every test in this assembly. CoordinateTypeTests
// and DpiHelperTests both set a non-1.0 scale and neither restored it, so later tests in this
// assembly inherited whatever the previous one left. Same hook, same reasoning, as EyeCtrl.App.Tests:
// see DpiHelperRestoreAttribute, and DpiHelperWriterIsolationTests in EyeCtrl.App.Tests, which
// requires this declaration for every test project holding a DpiHelper writer.
[assembly: DpiHelperRestore]

// The effect half; see DpiHelperScaleObserverAttribute.
[assembly: DpiHelperScaleObserver]

// Puts StringTable's process-global table back to embedded English after every test, and the
// observer reddens the first test that STARTS on a non-English global. Same hook/observer rule as
// DpiHelper above: a German-path test must use an injected table, or an exclusive collection if it
// must set the global. See StringTableIsolationTests in EyeCtrl.App.Tests.
[assembly: StringTableRestore]
[assembly: StringTableObserver]
