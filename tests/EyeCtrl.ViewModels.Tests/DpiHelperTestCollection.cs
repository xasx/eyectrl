namespace EyeCtrl.ViewModels.Tests;

/// <summary>
/// Serialises every ViewModels test class that reads or writes <c>DpiHelper</c>.
///
/// <c>DpiHelper</c> is a <c>public static class</c> whose <c>ScaleX</c>/<c>ScaleY</c> are
/// process-global mutable state, and <c>ActionMenuLayoutHelperTests</c> writes a non-1.0 scale
/// from a <c>[Theory]</c> parameter. xUnit runs test classes in parallel, so without this
/// collection that write can land in the middle of another class's test and change a number it
/// has already read.
///
/// The failure that motivated this was
/// <c>ActionMenuViewModelLayerTests.TheSecondLayer_ReusesTheSameFiveSlots</c>, which captures
/// item positions once per layer and compares them. <c>ActionMenuViewModel</c> computes those
/// positions eagerly through <c>DpiHelper.LogicalToPhysicalX</c>, so a scale flip between the
/// two captures makes an unchanged layout look changed: at 1.0 the top slot is (900, 245), at
/// 1.25 it is (900, 181.25). It failed roughly once in six full-solution runs.
///
/// Mirrors <c>AppDpiHelperTestCollection</c> in EyeCtrl.App.Tests. The two are separate names
/// because collections are scoped per test assembly.
///
/// Membership rule: any class that constructs a view model or helper reaching <c>DpiHelper</c>,
/// not just the ones that call <c>Initialize</c>. Readers are the victims, not the cause.
/// </summary>
[CollectionDefinition("DpiHelper Tests (ViewModels)", DisableParallelization = true)]
public class ViewModelsDpiHelperTestCollection
{
}
