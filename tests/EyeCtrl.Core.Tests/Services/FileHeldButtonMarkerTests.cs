namespace EyeCtrl.Core.Tests.Services;

using EyeCtrl.Core.Services;

/// <summary>
/// C3's cross-process record: the file's PRESENCE is the signal, written atomically so a reader in
/// another process (the Supervisor, or the next App start) never sees a half-written marker. These
/// tests use throwaway temp directories only — the real <c>%APPDATA%\EyeCtrl</c> marker is never
/// touched.
/// </summary>
public class FileHeldButtonMarkerTests
{
    [Fact]
    public void AFreshPath_IsNotSet()
    {
        using var temp = new TempDirectory();
        var marker = new FileHeldButtonMarker(temp.MarkerPath);

        Check.That(marker.IsSet).IsFalse();
    }

    [Fact]
    public void Set_WritesTheMarkerAndClear_RemovesIt()
    {
        using var temp = new TempDirectory();
        var marker = new FileHeldButtonMarker(temp.MarkerPath);

        marker.Set();
        Check.That(marker.IsSet).IsTrue();
        Check.That(File.Exists(temp.MarkerPath)).IsTrue();

        marker.Clear();
        Check.That(marker.IsSet).IsFalse();
    }

    [Fact]
    public void Set_IsIdempotent_AndLeavesOneFile()
    {
        // A second press before any release (the drop path failed to clear, then a new drag)
        // must leave exactly one marker, not accumulate anything a reader has to interpret.
        using var temp = new TempDirectory();
        var marker = new FileHeldButtonMarker(temp.MarkerPath);

        marker.Set();
        marker.Set();

        Check.That(marker.IsSet).IsTrue();
        Check.That(Directory.GetFiles(temp.Path)).HasSize(1);
    }

    [Fact]
    public void Clear_WhenAbsent_IsANoOp()
    {
        // Clear runs on every release path, including ones where no marker was ever written
        // (a drag whose setup failed before the press still calls ReleaseHeldButton) — and the
        // folder itself may not exist yet.
        using var temp = new TempDirectory();
        var marker = new FileHeldButtonMarker(temp.MarkerPath);

        Check.ThatCode(marker.Clear).DoesNotThrow();
        Check.That(marker.IsSet).IsFalse();
    }

    [Fact]
    public void Set_CreatesTheDirectoryAndLeavesNoTempFileBehind()
    {
        // The atomic write is temp-then-move: the move must replace the real name and the temp
        // must be gone afterwards, or a leftover could confuse a future reader.
        using var temp = new TempDirectory();
        var marker = new FileHeldButtonMarker(temp.MarkerPath);

        marker.Set();

        Check.That(Directory.Exists(temp.Path)).IsTrue();
        Check.That(Directory.GetFiles(temp.Path)).HasSize(1);
        Check.That(Path.GetFileName(Directory.GetFiles(temp.Path)[0])).IsEqualTo("drag-left-button-held");
    }

    [Fact]
    public void Set_OverAnExistingMarker_ReplacesIt()
    {
        // Crash-then-relaunch-then-crash: the marker already exists when the second press writes.
        using var temp = new TempDirectory();
        var marker = new FileHeldButtonMarker(temp.MarkerPath);
        marker.Set();

        marker.Set();

        Check.That(marker.IsSet).IsTrue();
    }

    [Fact]
    public void DefaultPath_IsTheSharedAppDataMarker()
    {
        // Production wiring: the App (drag press/release, startup backstop) and the Supervisor
        // (post-kill release) must agree on ONE file. Both take this default.
        Check.That(FileHeldButtonMarker.DefaultPath).IsEqualTo(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EyeCtrl",
                FileHeldButtonMarker.FileName));
        Check.That(new FileHeldButtonMarker().Path).IsEqualTo(FileHeldButtonMarker.DefaultPath);
    }

    /// <summary>A unique directory, deleted on dispose so the suite leaves nothing in %TEMP%.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "eyectrl_c3_" + Guid.NewGuid().ToString("N"));

        public string MarkerPath => System.IO.Path.Combine(Path, FileHeldButtonMarker.FileName);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort; a locked file must not fail the test that just passed.
            }
        }
    }
}
