namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NFluent;
using Xunit;

/// <summary>
/// D3 source guard: inside <c>src/</c>, <see cref="EyeCtrl.Core.Models.RingLabelMap"/> may be read
/// only by <c>RingLabelSource</c> (which resolves it into <c>RingLabelSource.Overlays</c> for the
/// live table and <c>RingLabelSource.English</c> for the embedded one). A second production
/// reader is a label site that cannot be keyed per surface — the exact situation the
/// <c>RingLabelSource</c> seam exists to prevent.
///
/// Scans live code, not comments: doc references to the map are fine, a call is not.
/// </summary>
public class RingLabelMapReaderScanTests
{
    private static readonly Regex MapRead = new(@"RingLabelMap\.", RegexOptions.Compiled);

    /// <summary>The one file allowed to name the map in code — the English source itself.</summary>
    private const string AllowedFile = "src/EyeCtrl.Core/ActionMenu/RingLabelSource.cs";

    [Fact]
    public void InSrc_RingLabelMapIsReadOnlyByRingLabelSource()
    {
        var root = SourceScanner.RepoRoot().FullName;
        var srcDir = Path.Combine(root, "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == AllowedFile) continue;

            if (MapRead.IsMatch(SourceScanner.StripComments(File.ReadAllText(file))))
                offenders.Add(relative);
        }

        Check.WithCustomMessage(
                "RingLabelMap is read outside RingLabelSource. Route the site through a "
                + "RingLabelSource (the host's labels) instead:\n  " + string.Join("\n  ", offenders))
             .That(offenders).IsEmpty();
    }
}
