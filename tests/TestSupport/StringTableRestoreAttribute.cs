namespace EyeCtrl.Tests.TestSupport;

using System.Reflection;
using EyeCtrl.Core.Localization;
using Xunit.v3;

/// <summary>
/// Puts <see cref="StringTable"/>'s process-global table back to the compiled English default after
/// EVERY test in whatever assembly links this file. Declared at assembly level so no test can leak
/// a German (or otherwise activated) global into a later test, whether it writes the global
/// directly or through production code — the same hook-over-per-class-discipline reasoning as
/// <c>DpiHelperRestoreAttribute</c>.
///
/// <para>
/// It cannot throw — a fault here would land on every test in the assembly rather than on one.
/// LINKED into every Core-referencing test project, with one <c>[assembly: StringTableRestore]</c>
/// per project, not copied: a second copy is a second rule. Which projects must declare it is
/// pinned by <c>StringTableIsolationTests.EveryCoreReferencingTestProject_InstallsBothGuards</c>.
/// </para>
/// </summary>
public sealed class StringTableRestoreAttribute : BeforeAfterTestAttribute
{
    /// <inheritdoc/>
    public override void After(MethodInfo methodUnderTest, IXunitTest test) =>
        StringTable.ResetToEmbeddedEnglish();
}
