namespace EyeCtrl.Setup.Tests;

using EyeCtrl.Setup;

/// <summary>
/// N2: the production wiring must hand the update's package folder to <c>SetupUi</c>, and must
/// hand nothing to every other job — especially the elevated jobs, which are not allowed to read
/// the inbox at all.
/// </summary>
public class SetupCompositionTests
{
    [Fact]
    public void PreferredStringsDirectory_IsThePackageFolderOnlyForUpdate()
    {
        var update = SetupCommandLine.Parse(["update", @"C:\inbox\2.0.0"]);
        Check.That(SetupComposition.PreferredStringsDirectory(update)).IsEqualTo(@"C:\inbox\2.0.0");

        var otherJobs = new[]
        {
            new[] { "rollback" },
            new[] { "uninstall" },
            new[] { "uninstall", "--quiet" },
            new[] { "uninstall", "--delete" },
            new[] { "repair" },
            new[] { "status" },
            new[] { "--internal", "apply-update", "--run-id", "r1" },
            new[] { "--internal", "prepare-update", @"C:\inbox\2.0.0", "--run-id", "r1" },
            new[] { "--internal", "apply-rollback", "--run-id", "r1" },
            new[] { "--internal", "uninstall", "--keep", "--run-id", "r1" },
            new[] { "--internal", "repair", "--run-id", "r1" },
        };

        foreach (var args in otherJobs)
        {
            var command = SetupCommandLine.Parse(args);
            Check.That(command.Error).IsNull();
            Check.WithCustomMessage(string.Join(" ", args) + " must not read a package's strings.")
                .That(SetupComposition.PreferredStringsDirectory(command)).IsNull();
        }
    }
}
