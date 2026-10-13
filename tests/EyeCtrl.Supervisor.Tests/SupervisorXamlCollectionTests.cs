namespace EyeCtrl.Supervisor.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Keeps <see cref="SupervisorXamlCollection"/> complete: every test file that constructs a class
/// backed by Supervisor XAML must put its class in that collection, or it races the others inside
/// <c>Application.LoadComponent</c> (T09 review-03, F1). The XAML-backed classes are read from the
/// Supervisor project's <c>.xaml</c> files, so a new window is covered without editing this test.
/// </summary>
public class SupervisorXamlCollectionTests
{
    [Fact]
    public void EveryTestFileThatLoadsSupervisorXaml_IsInTheCollection()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EyeCtrl.slnx")))
            root = root.Parent;
        Check.WithCustomMessage("The repository root (EyeCtrl.slnx) was not found above the test binaries.").That(root).IsNotNull();

        var xamlClasses = Directory.GetFiles(Path.Combine(root!.FullName, "src", "EyeCtrl.Supervisor"), "*.xaml", SearchOption.AllDirectories)
            .Select(f => Regex.Match(File.ReadAllText(f), "x:Class=\"(?:[\\w.]+\\.)?(\\w+)\""))
            .Where(m => m.Success && m.Groups[1].Value != "App")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Check.WithCustomMessage("No XAML-backed Supervisor class was found: this guard would check nothing.")
            .That(xamlClasses).Contains("FallenBackDialog", "SupervisorSetupWindow");

        var constructs = new Regex(@"new\s+(" + string.Join("|", xamlClasses) + @")\s*\(");
        var testRoot = Path.Combine(root.FullName, "tests", "EyeCtrl.Supervisor.Tests");
        var loaders = Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => constructs.IsMatch(File.ReadAllText(f)))
            .ToList();
        Check.WithCustomMessage("No test file constructs Supervisor XAML: this guard would check nothing.").That(loaders).Not.IsEmpty();

        var outside = loaders
            .Where(f => !File.ReadAllText(f).Contains("[Collection(SupervisorXamlCollection.Name)]", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(testRoot, f))
            .ToList();
        Check.WithCustomMessage(
                "These test files load Supervisor XAML outside the \"Supervisor XAML\" collection, so they can race "
                + "the others inside Application.LoadComponent:\n  " + string.Join("\n  ", outside))
            .That(outside).IsEmpty();
    }
}
