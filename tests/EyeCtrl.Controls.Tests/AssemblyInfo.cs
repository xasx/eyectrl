using EyeCtrl.Tests.TestSupport;

// Puts StringTable's process-global table back to embedded English after every test, and the
// observer reddens the first test that STARTS on a non-English global. The Controls test suite
// reads StringTable.Current through the XAML markup extension, so a leaked German table would
// change what later tests render. See StringTableIsolationTests in EyeCtrl.App.Tests.
[assembly: StringTableRestore]
[assembly: StringTableObserver]
