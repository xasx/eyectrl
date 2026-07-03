namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;

public class SettingsSnapshotsTests
{
    [Fact]
    public void KeyboardStackSnapshot_UnrelatedSettingChange_SnapshotsAreEqual()
    {
        var s = new EyeCtrlSettings();
        var before = KeyboardStackSnapshot.Capture(s);
        s.Cursor.CircleOpacity = 0.42;
        s.Toolbar.Size = 96;
        var after = KeyboardStackSnapshot.Capture(s);
        Check.That(after).IsEqualTo(before);
    }

    [Fact]
    public void KeyboardStackSnapshot_PredictionRelevantChange_SnapshotsDiffer()
    {
        var s = new EyeCtrlSettings();
        var before = KeyboardStackSnapshot.Capture(s);
        s.Keyboard.MLModelSize = "1.7B";
        Check.That(KeyboardStackSnapshot.Capture(s)).IsNotEqualTo(before);

        before = KeyboardStackSnapshot.Capture(s);
        s.Dwell.KeyboardKeyDwellMs += 25;
        Check.That(KeyboardStackSnapshot.Capture(s)).IsNotEqualTo(before);
    }

    [Fact]
    public void ToolbarSnapshot_OnlyToolbarFieldsMatter()
    {
        var s = new EyeCtrlSettings();
        var before = ToolbarSnapshot.Capture(s);
        s.Keyboard.KeySizeMultiplier = 1.5;
        Check.That(ToolbarSnapshot.Capture(s)).IsEqualTo(before);

        s.Toolbar.DockedEdge = "Left";
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);

        before = ToolbarSnapshot.Capture(s);
        s.Dwell.ToolbarButtonDwellMs += 50;
        Check.That(ToolbarSnapshot.Capture(s)).IsNotEqualTo(before);
    }
}
