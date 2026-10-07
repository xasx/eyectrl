namespace EyeCtrl.Tests.TestSupport;

using System;
using System.Collections.Concurrent;
using System.Reflection;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using Xunit.v3;

/// <summary>
/// Fails the first test that STARTS with a non-English <see cref="StringTable.Current"/>, naming
/// the test and the language it observed. It is the effect half of
/// <see cref="StringTableRestoreAttribute"/>: the restore hook makes a leak harmless, and this
/// observer makes a restore that stopped working loud — so a mutation that disables the reset
/// reddens the suite instead of silently feeding every later test German output.
///
/// <para>
/// Why it observes in <c>Before</c> and throws in <c>After</c>, never at the point of observation:
/// a throw from <c>Before</c> aborts xUnit's pre-invoke loop, so every attribute later in the list
/// also never runs its <c>After</c> half — the same measured reason as
/// <c>DpiHelperScaleObserverAttribute</c>. Recording in <c>Before</c> and throwing from <c>After</c>
/// keeps each attribute's pair intact, and the <c>After</c> half resets to English first so a
/// one-off leak does not also fail every later test on the residue.
/// </para>
///
/// <para>
/// LINKED into every Core-referencing test project, with one <c>[assembly: StringTableObserver]</c>
/// per project. Disabling it silently leaves the restore protecting the suite but removes the
/// detection; that top layer stays documented rather than guarded by a detector for the detector.
/// </para>
/// </summary>
public sealed class StringTableObserverAttribute : BeforeAfterTestAttribute
{
    private static readonly ConcurrentDictionary<string, UiLanguage> CorruptedStarts = new();

    /// <inheritdoc/>
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        var language = StringTable.Current.Language;
        if (language == UiLanguage.English) return;

        CorruptedStarts[test.UniqueID] = language;
    }

    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!CorruptedStarts.TryRemove(test.UniqueID, out var observed)) return;

        // Reset before throwing: one failed restore must not fail every later test too.
        StringTable.ResetToEmbeddedEnglish();

        throw new InvalidOperationException(
            $"{methodUnderTest.DeclaringType?.FullName}.{methodUnderTest.Name} started with "
            + $"StringTable.Current in {observed}, so the per-test restore did not run for a previous "
            + "test and this test read a leaked language — any text it asserts is suspect. Check "
            + "StringTableRestoreAttribute.After: it must reset to embedded English unconditionally, "
            + "for every test. If this is a test that deliberately activates a German global, put it "
            + "in a collection declared [CollectionDefinition(..., DisableParallelization = true)] in "
            + "the same test project.");
    }
}
