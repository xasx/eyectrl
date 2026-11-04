namespace EyeCtrl.Setup.Tests;

using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using EyeCtrl.Setup;

public class SetupManifestTests
{
    private const string AssemblyNamespace = "urn:schemas-microsoft-com:asm.v1";
    private const string ApplicationNamespace = "urn:schemas-microsoft-com:asm.v3";
    private const string DpiAwareNamespace = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";
    private const string DpiAwarenessNamespace = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    [Fact]
    public void TheSetupManifest_DeclaresPerMonitorV2_AndStaysAsInvokerWithoutUiAccess()
    {
        var document = XDocument.Parse(File.ReadAllText(ManifestPath));

        AssertPerMonitorV2(document);

        var privilege = document.Descendants()
            .Single(e => e.Name.LocalName == "requestedExecutionLevel");

        Check.That((string?)privilege.Attribute("level")).IsEqualTo("asInvoker");
        Check.That((string?)privilege.Attribute("uiAccess")).IsEqualTo("false");
    }

    [Fact]
    public void TheProject_SelectsThatManifest_AndNeverTheUiAccessOne()
    {
        var project = XDocument.Load(ProjectPath);
        var manifests = project.Descendants()
            .Where(e => e.Name.LocalName == "ApplicationManifest")
            .Select(e => Path.GetFileName(e.Value.Trim()))
            .ToList();

        Check.That(manifests).ContainsExactly("app.manifest");
        Check.That(File.ReadAllText(ProjectPath)).DoesNotContain("app.uiaccess.manifest");
        Check.That(project.Descendants().Any(e =>
            e.Name.LocalName.Equals("EyeCtrlUiAccess", StringComparison.OrdinalIgnoreCase))).IsFalse();
    }

    [Fact]
    public void TheBuiltExe_EmbedsTheManifestByteForByte_WithPerMonitorV2()
    {
        // The ratchet reads the embedded RT_MANIFEST out of the built apphost with PEReader — the
        // PE resource table is read from the file bytes; nothing is mapped or started. Without the
        // declaration the first frames are laid out DIP-for-pixel and then resized (1344x807 ->
        // 2688x1613 at 200 % in the S6c rerun), so this is a silent, visible-only regression.
        var exe = Path.Combine(AppContext.BaseDirectory, "EyeCtrl.Setup.exe");
        Check.WithCustomMessage($"the built Setup apphost is missing at {exe}")
            .That(File.Exists(exe)).IsTrue();

        var embedded = EmbeddedManifest.Read(exe);
        AssertSameBytes(File.ReadAllBytes(ManifestPath), embedded,
            "the built Setup apphost must embed app.manifest byte for byte");

        var document = XDocument.Parse(Decode(embedded));
        AssertPerMonitorV2(document);

        var privilege = document.Descendants()
            .Single(e => e.Name.LocalName == "requestedExecutionLevel");
        Check.That((string?)privilege.Attribute("level")).IsEqualTo("asInvoker");
        Check.That((string?)privilege.Attribute("uiAccess")).IsEqualTo("false");
        Check.That(Decode(embedded)).DoesNotContain("uiAccess=\"true\"");
    }

    [Fact]
    public void TheSolution_ListsSetupAndItsTests()
    {
        var solution = File.ReadAllText(SolutionPath);

        Check.That(solution).Contains("src/EyeCtrl.Setup/EyeCtrl.Setup.csproj");
        Check.That(solution).Contains("tests/EyeCtrl.Setup.Tests/EyeCtrl.Setup.Tests.csproj");
    }

    [Fact]
    public void SetupStartsTheAppWithShellExecute_AndNeverCreateProcess()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "WindowsProcessControl.cs"));

        Check.That(source).Contains("UseShellExecute = true");
        Check.That(source).DoesNotContain("UseShellExecute = false");
    }

    [Fact]
    public void TheElevatedBroker_UsesShellExecuteRunas()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "ShellExecuteElevationBroker.cs"));

        Check.That(source).Contains("UseShellExecute = true");
        Check.That(source).Contains("Verb = \"runas\"");
    }

    [Fact]
    public void TheImageCheck_UsesOnlyLimitedQueryRights()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "WindowsProcessControl.cs"));

        Check.That(source).Contains("QueryFullProcessImageName");
        Check.That(source).Contains("ProcessQueryLimitedInformation = 0x1000");
        Check.That(source).DoesNotContain("PROCESS_QUERY_INFORMATION");    }

    [Fact]
    public void TheProcessSeam_HasNoRestartRequest()
    {
        // Plan §10: Setup never sends RestartRequested. The seam has no such operation at all, so
        // no workflow can send one even by accident; the clean path only waits for the App.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Abstractions", "ProcessSeams.cs"));

        Check.That(source).DoesNotContain("Restart");
    }

    [Fact]
    public void TheHeldButtonRelease_IsCoresImplementation()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "SetupInputSimulator.cs"));

        Check.That(source).Contains("new HeldButtonRelease(");
        Check.That(source).Contains("new FileHeldButtonMarker()");
    }

    [Fact]
    public void SetupShipsNoLooseFileOfItsOwn()
    {
        // S6b's publish script stops when two publish outputs share a name with different bytes.
        // Setup carries no Content/None copy items: it reads the App's data from the installed
        // folder instead of shipping a second copy.
        var project = XDocument.Load(ProjectPath);
        var copied = project.Descendants()
            .Where(e => e.Name.LocalName is "Content" or "None")
            .Where(e => e.Attribute("CopyToOutputDirectory") is not null
                || e.Attribute("CopyToPublishDirectory") is not null)
            .Select(e => e.Name.LocalName + ":" + (string?)e.Attribute("Include"))
            .ToList();

        Check.That(copied).IsEmpty();
    }

    [Fact]
    public void TheKillPath_NeverKillsAProcessTree()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Setup", "Windows", "WindowsProcessControl.cs"));

        Check.That(source).DoesNotContain(".Kill(entireProcessTree: true)");
    }

    [Fact]
    public void ThePinnedThumbprint_IsThePlansValue()
    {
        Check.That(SetupConstants.PinnedThumbprint)
            .IsEqualTo("C1039769A2D9ED01F93CEBC78B61176EE6B12F91");
    }

    // ── Manifest assertions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts the whole DPI block, not just a substring: the awareness context, its PerMonitor
    /// fallback, and that the element sits in the manifest's <c>&lt;application&gt;</c> settings
    /// where Windows reads it. A declaration tucked into another element has no effect.
    /// </summary>
    private static void AssertPerMonitorV2(XDocument document)
    {
        var assembly = document.Root!;
        Check.That(assembly.Name.NamespaceName).IsEqualTo(AssemblyNamespace);
        Check.WithCustomMessage("the manifest gained an element besides identity, trustInfo and application")
            .That(assembly.Elements().Select(e => e.Name.LocalName))
            .ContainsExactly("assemblyIdentity", "trustInfo", "application");

        var application = assembly.Element(XName.Get("application", ApplicationNamespace));
        Check.WithCustomMessage("the manifest must declare <application> in urn:schemas-microsoft-com:asm.v3")
            .That(application).IsNotNull();

        var windowsSettings = application!.Element(XName.Get("windowsSettings", ApplicationNamespace));
        Check.WithCustomMessage("the manifest must carry one <windowsSettings> under <application>")
            .That(windowsSettings).IsNotNull();

        var dpiAware = windowsSettings!.Element(XName.Get("dpiAware", DpiAwareNamespace));
        Check.WithCustomMessage("the manifest must carry <dpiAware>true/pm</dpiAware> for older Windows")
            .That(dpiAware).IsNotNull();
        Check.That(dpiAware!.Value).IsEqualTo("true/pm");

        var dpiAwareness = windowsSettings.Element(XName.Get("dpiAwareness", DpiAwarenessNamespace));
        Check.WithCustomMessage("the manifest must carry PerMonitorV2 with its PerMonitor fallback")
            .That(dpiAwareness).IsNotNull();
        Check.That(dpiAwareness!.Value).IsEqualTo("PerMonitorV2, PerMonitor");
    }

    private static string Decode(byte[] manifest) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(
            manifest.AsSpan(manifest.Length >= 3 && manifest[0] == 0xEF && manifest[1] == 0xBB && manifest[2] == 0xBF ? 3 : 0));

    private static void AssertSameBytes(byte[] expected, byte[] actual, string because)
    {
        var expectedHash = Convert.ToHexString(SHA256.HashData(expected));
        var actualHash = Convert.ToHexString(SHA256.HashData(actual));
        Check.WithCustomMessage(
                $"{because} (expected {expected.Length} bytes/{expectedHash}, actual {actual.Length} bytes/{actualHash})")
            .That(actual.SequenceEqual(expected)).IsTrue();
    }

    /// <summary>
    /// Reads RT_MANIFEST out of an apphost without loading it: the PE resource directory is walked
    /// from the file bytes (the same approach as the S2 UiAccessManifestTests).
    /// </summary>
    private static class EmbeddedManifest
    {
        private const int ResourceTypeManifest = 24;
        private const int ManifestResourceId = 1;

        internal static byte[] Read(string exePath)
        {
            using var stream = File.OpenRead(exePath);
            using var image = new PEReader(stream);
            var headers = image.PEHeaders;
            var directory = headers.PEHeader!.ResourceTableDirectory;
            Check.WithCustomMessage($"{Path.GetFileName(exePath)} has no PE resource directory")
                .That(directory.RelativeVirtualAddress).IsStrictlyGreaterThan(0);

            var sectionIndex = -1;
            for (var i = 0; i < headers.SectionHeaders.Length; i++)
            {
                var section = headers.SectionHeaders[i];
                var end = section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (directory.RelativeVirtualAddress >= section.VirtualAddress
                    && directory.RelativeVirtualAddress < end)
                {
                    sectionIndex = i;
                    break;
                }
            }
            Check.WithCustomMessage($"{Path.GetFileName(exePath)}: the resource directory is outside every section")
                .That(sectionIndex >= 0).IsTrue();

            var sectionBase = headers.SectionHeaders[sectionIndex].VirtualAddress;
            var block = image.GetSectionData(directory.RelativeVirtualAddress);

            // Three levels: type (RT_MANIFEST) -> name/id (1) -> language (usually neutral, 0).
            var typeEntry = FindResourceEntry(block, directory.RelativeVirtualAddress - sectionBase, ResourceTypeManifest);
            var nameEntry = FindResourceEntry(block, (int)(typeEntry & 0x7FFF_FFFF), ManifestResourceId);
            var languageEntry = FirstResourceEntry(block, (int)(nameEntry & 0x7FFF_FFFF));
            var dataEntryOffset = (int)(languageEntry & 0x7FFF_FFFF);

            var reader = block.GetReader();
            reader.Offset = dataEntryOffset;
            var dataRva = reader.ReadInt32();
            var size = reader.ReadInt32();

            // OffsetToData is an image-relative RVA, and the block starts at the containing section.
            return block.GetContent(dataRva - sectionBase, size).ToArray();
        }

        private static uint FindResourceEntry(PEMemoryBlock block, int directoryOffset, int id)
        {
            var reader = block.GetReader();
            reader.Offset = directoryOffset + 12; // past Characteristics, TimeDateStamp, Major/Minor
            var named = reader.ReadUInt16();
            var ids = reader.ReadUInt16();

            for (var i = 0; i < named + ids; i++)
            {
                reader.Offset = directoryOffset + 16 + (i * 8);
                var name = reader.ReadUInt32();
                var dataOffset = reader.ReadUInt32();

                if ((name & 0x8000_0000) != 0) continue; // a named entry; RT_MANIFEST and id 1 are numeric
                if (name != (uint)id) continue;
                return dataOffset;
            }

            throw new InvalidOperationException($"resource id {id} not found in the PE resource directory");
        }

        private static uint FirstResourceEntry(PEMemoryBlock block, int directoryOffset)
        {
            var reader = block.GetReader();
            reader.Offset = directoryOffset + 12;
            var named = reader.ReadUInt16();
            var ids = reader.ReadUInt16();
            if (named + ids == 0)
                throw new InvalidOperationException("the PE manifest has no language entry");

            reader.Offset = directoryOffset + 16;
            _ = reader.ReadUInt32(); // name/id
            return reader.ReadUInt32();
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EyeCtrl.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find EyeCtrl.slnx above " + AppContext.BaseDirectory);
    }

    private static string ProjectPath =>
        Path.Combine(RepoRoot(), "src", "EyeCtrl.Setup", "EyeCtrl.Setup.csproj");

    private static string ManifestPath =>
        Path.Combine(RepoRoot(), "src", "EyeCtrl.Setup", "app.manifest");

    private static string SolutionPath => Path.Combine(RepoRoot(), "EyeCtrl.slnx");
}
