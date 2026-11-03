namespace EyeCtrl.Architecture.Tests;

using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml.Linq;

/// <summary>
/// C9 S2 ratchets for the UIAccess build switch. Headless by construction: these tests read the
/// two manifest files, parse the two csprojs, and parse the manifest resource out of the built
/// apphosts. Nothing here starts a process or loads an image — the PE resource directory is read
/// straight from the file bytes.
///
/// <para>
/// Why a ratchet rather than a build check in CI: the failure this pins is silent. A default
/// build that embeds <c>uiAccess="true"</c> still runs; it just asks Windows for a privilege it
/// cannot have, and an install outside Program Files would quietly change behaviour. Likewise
/// <c>IncludeNativeLibrariesForSelfExtract</c> would extract native DLLs into a user-writable
/// %TEMP% directory and load them into a High-IL process.
/// </para>
/// </summary>
public class UiAccessManifestTests
{
    private const string AssemblyNamespace = "urn:schemas-microsoft-com:asm.v1";
    private const string TrustNamespace = "urn:schemas-microsoft-com:asm.v2";
    private const string PrivilegesNamespace = "urn:schemas-microsoft-com:asm.v3";

    private static readonly string[] SelfExtractionFlags =
    [
        "IncludeNativeLibrariesForSelfExtract",
        "IncludeAllContentForSelfExtract",
    ];

    [Fact]
    public void TheTwoManifests_DifferOnlyInTheUiAccessAttribute()
    {
        var app = File.ReadAllText(AppManifestPath);
        var uiAccess = File.ReadAllText(UiAccessManifestPath);

        Check.WithCustomMessage("app.manifest must carry exactly one uiAccess attribute")
            .That(Occurrences(app, "uiAccess=\"false\"")).IsEqualTo(1);
        Check.WithCustomMessage("app.uiaccess.manifest must carry exactly one uiAccess attribute")
            .That(Occurrences(uiAccess, "uiAccess=\"true\"")).IsEqualTo(1);

        // The whole file, not just the attribute: a second edit (a DPI element, a raised
        // requestedExecutionLevel, an added dependency) fails here.
        Check.WithCustomMessage(
                "app.uiaccess.manifest must be app.manifest with uiAccess flipped to true and nothing else changed")
            .That(uiAccess).IsEqualTo(app.Replace("uiAccess=\"false\"", "uiAccess=\"true\"", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultManifest_IsTodaysSdkDefault_ElementForElement()
    {
        // Today's embedded manifest, read back from a built EyeCtrl.App.exe before S2 added the
        // file: the SDK default, asInvoker with uiAccess="false", and nothing else.
        AssertIsSdkDefaultManifest(File.ReadAllText(AppManifestPath), expectedUiAccess: "false");
    }

    [Fact]
    public void TheDefaultBuild_EmbedsTheFileByteForByte_WithUiAccessFalse()
    {
        var embedded = ReadEmbeddedManifest(Path.Combine(AppContext.BaseDirectory, "EyeCtrl.App.exe"));

        AssertSameBytes(File.ReadAllBytes(AppManifestPath), embedded, "the default App build must embed app.manifest");

        var text = Decode(embedded);
        Check.That(text).Contains("uiAccess=\"false\"");
        Check.That(text).DoesNotContain("uiAccess=\"true\"");
    }

    [Fact]
    public void TheBuildSwitch_DefaultsToFalse_AndSelectsThePlainManifest()
    {
        var project = XDocument.Load(AppProjectPath);

        var switchValue = project.Descendants()
            .Where(e => e.Name.LocalName.Equals("EyeCtrlUiAccess", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Check.WithCustomMessage(
                "EyeCtrl.App.csproj must declare EyeCtrlUiAccess once, defaulted to false by an " +
                "empty-value condition so an explicit -p:EyeCtrlUiAccess=true still wins")
            .That(switchValue).HasSize(1);
        Check.That(switchValue[0].Value.Trim()).IsEqualTo("false");
        Check.That(switchValue[0].Attribute("Condition")?.Value ?? string.Empty)
            .Contains("'$(EyeCtrlUiAccess)' == ''");

        var selected = project.Descendants()
            .Where(e => e.Name.LocalName == "ApplicationManifest")
            .ToDictionary(e => e.Attribute("Condition")?.Value ?? string.Empty, e => e.Value.Trim());

        Check.WithCustomMessage("the default arm must select app.manifest")
            .That(selected.Values.Any(v => Path.GetFileName(v) == "app.manifest")).IsTrue();
        Check.WithCustomMessage("the uiAccess arm must select app.uiaccess.manifest")
            .That(selected.Values.Any(v => Path.GetFileName(v) == "app.uiaccess.manifest")).IsTrue();
        Check.WithCustomMessage("both manifest arms must exist; a missing default arm is how a stray uiAccess build hides")
            .That(selected).HasSize(2);
    }

    [Fact]
    public void TheSupervisor_NeverGetsTheUiAccessManifest()
    {
        var project = XDocument.Load(SupervisorProjectPath);

        Check.WithCustomMessage("the Supervisor must not select an application manifest at all")
            .That(project.Descendants().Any(e => e.Name.LocalName == "ApplicationManifest")).IsFalse();
        Check.WithCustomMessage("the Supervisor must not read the EyeCtrlUiAccess switch")
            .That(project.Descendants().Any(e =>
                e.Name.LocalName.Equals("EyeCtrlUiAccess", StringComparison.OrdinalIgnoreCase))).IsFalse();
        Check.WithCustomMessage("the Supervisor csproj must not mention the uiAccess manifest")
            .That(File.ReadAllText(SupervisorProjectPath)).DoesNotContain("app.uiaccess.manifest");

        // And the built exe proves it: the Supervisor embeds exactly the same default manifest as
        // the App's default build, so every build of it is asInvoker/uiAccess="false".
        var supervisorManifest = ReadEmbeddedManifest(Path.Combine(AppContext.BaseDirectory, "EyeCtrl.Supervisor.exe"));
        AssertSameBytes(
            File.ReadAllBytes(AppManifestPath),
            supervisorManifest,
            "the Supervisor build must embed the same non-uiAccess default manifest");
        Check.That(Decode(supervisorManifest)).Contains("uiAccess=\"false\"");
    }

    [Fact]
    public void NeitherAppNorSupervisor_EnablesSelfExtraction()
    {
        foreach (var projectPath in new[] { AppProjectPath, SupervisorProjectPath })
        {
            var project = XDocument.Load(projectPath);
            var enabled = project.Descendants()
                .Where(e => SelfExtractionFlags.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                .Where(e => !e.Value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
                .Select(e => $"{Path.GetFileName(projectPath)}: <{e.Name.LocalName}>{e.Value.Trim()}</{e.Name.LocalName}>")
                .ToList();

            Check.WithCustomMessage(
                    "self-extraction loads native DLLs from a user-writable %TEMP% into the High-IL process: "
                    + string.Join(", ", enabled))
                .That(enabled).IsEmpty();
        }
    }

    // ── Manifest assertions ───────────────────────────────────────────────────────────────

    private static void AssertIsSdkDefaultManifest(string text, string expectedUiAccess)
    {
        var document = XDocument.Parse(text);

        Check.WithCustomMessage("the manifest must keep the SDK's XML declaration")
            .That(document.Declaration?.Version).IsEqualTo("1.0");
        Check.That(document.Declaration?.Encoding).IsEqualTo("UTF-8");
        Check.That(document.Declaration?.Standalone).IsEqualTo("yes");

        var assembly = document.Root!;
        Check.That(assembly.Name.LocalName).IsEqualTo("assembly");
        Check.That(assembly.Name.NamespaceName).IsEqualTo(AssemblyNamespace);
        Check.That((string?)assembly.Attribute("manifestVersion")).IsEqualTo("1.0");
        Check.That(assembly.Elements().Select(e => e.Name.LocalName))
            .ContainsExactly("assemblyIdentity", "trustInfo");

        var identity = assembly.Element(XName.Get("assemblyIdentity", AssemblyNamespace))!;
        Check.That((string?)identity.Attribute("version")).IsEqualTo("1.0.0.0");
        Check.That((string?)identity.Attribute("name")).IsEqualTo("MyApplication.app");
        Check.That(identity.Elements()).IsEmpty();

        var trustInfo = assembly.Element(XName.Get("trustInfo", TrustNamespace))!;
        Check.That(trustInfo.Elements().Select(e => e.Name.LocalName)).ContainsExactly("security");

        var security = trustInfo.Element(XName.Get("security", TrustNamespace))!;
        Check.That(security.Elements().Select(e => e.Name.LocalName)).ContainsExactly("requestedPrivileges");

        var privileges = security.Element(XName.Get("requestedPrivileges", PrivilegesNamespace))!;
        Check.That(privileges.Elements().Select(e => e.Name.LocalName))
            .ContainsExactly("requestedExecutionLevel");

        var level = privileges.Element(XName.Get("requestedExecutionLevel", PrivilegesNamespace))!;
        Check.That((string?)level.Attribute("level")).IsEqualTo("asInvoker");
        Check.That((string?)level.Attribute("uiAccess")).IsEqualTo(expectedUiAccess);
        Check.That(level.Elements()).IsEmpty();
        Check.That(level.Attributes().Select(a => a.Name.LocalName).OrderBy(n => n, StringComparer.Ordinal))
            .ContainsExactly("level", "uiAccess");
    }

    // ── PE resource reading (no load, no execution) ──────────────────────────────────────

    private const int ResourceTypeManifest = 24;
    private const int ManifestResourceId = 1;

    /// <summary>
    /// Reads the RT_MANIFEST resource out of an apphost without loading it: walks the PE resource
    /// directory from the file bytes. <see cref="PEReader"/> never maps or runs the image.
    /// </summary>
    private static byte[] ReadEmbeddedManifest(string exePath)
    {
        Check.WithCustomMessage($"built apphost not found at {exePath} — build the solution first")
            .That(File.Exists(exePath)).IsTrue();

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
        // GetSectionData takes an RVA and returns the containing section's bytes; passing the
        // section index would read RVA 5 (inside the headers) and yield an empty block.
        var block = image.GetSectionData(directory.RelativeVirtualAddress);

        // Three levels: type (RT_MANIFEST) → name/id (1) → language (usually neutral, 0).
        var typeEntry = FindResourceEntry(block, directory.RelativeVirtualAddress - sectionBase, ResourceTypeManifest);
        var nameEntry = FindResourceEntry(block, (int)(typeEntry & 0x7FFF_FFFF), ManifestResourceId);
        var languageEntry = FirstResourceEntry(block, (int)(nameEntry & 0x7FFF_FFFF));
        var dataEntryOffset = (int)(languageEntry & 0x7FFF_FFFF);

        var reader = block.GetReader();
        reader.Offset = dataEntryOffset;
        var dataRva = reader.ReadInt32();
        var size = reader.ReadInt32();

        // OffsetToData is an image-relative RVA, and the block starts at the containing section.
        var dataOffset = dataRva - sectionBase;
        return block.GetContent(dataOffset, size).ToArray();
    }

    /// <summary>
    /// Finds an id entry in one resource directory level and returns its raw OffsetToData: the
    /// high bit distinguishes the next directory level from a data entry.
    /// </summary>
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

    /// <summary>
    /// Returns the first entry of a directory level (the language level; the manifest has one,
    /// usually the neutral language 0).
    /// </summary>
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

    // ── Helpers ─────────────────────────────────────────────────────────────────────────

    private static string Decode(byte[] manifest) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(
            manifest.AsSpan(manifest.Length >= 3 && manifest[0] == 0xEF && manifest[1] == 0xBB && manifest[2] == 0xBF ? 3 : 0));

    private static void AssertSameBytes(byte[] expected, byte[] actual, string because)
    {
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected));
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual));
        Check.WithCustomMessage(
                $"{because} (expected {expected.Length} bytes/{expectedHash}, actual {actual.Length} bytes/{actualHash})")
            .That(actual.SequenceEqual(expected)).IsTrue();
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repository root (EyeCtrl.slnx) above " + AppContext.BaseDirectory);
    }

    private static string AppProjectPath => Path.Combine(RepoRoot(), "src", "EyeCtrl.App", "EyeCtrl.App.csproj");
    private static string AppManifestPath => Path.Combine(RepoRoot(), "src", "EyeCtrl.App", "app.manifest");
    private static string UiAccessManifestPath => Path.Combine(RepoRoot(), "src", "EyeCtrl.App", "app.uiaccess.manifest");
    private static string SupervisorProjectPath =>
        Path.Combine(RepoRoot(), "src", "EyeCtrl.Supervisor", "EyeCtrl.Supervisor.csproj");
}
