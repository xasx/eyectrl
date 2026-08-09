namespace EyeCtrl.Platform.Tests.Models;

using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;

public class QuickActionPresetsTests
{
    [Fact]
    public void KeyboardShortcuts_AreNonEmpty_AndHaveUniqueLabels()
    {
        var labels = QuickActionPresets.KeyboardShortcuts.Select(p => p.Label).ToList();
        Check.That(labels).Not.IsEmpty();
        Check.That(labels.Distinct().Count()).IsEqualTo(labels.Count);
    }

    [Fact]
    public void EveryKeyboardPreset_TerminalKey_ResolvesToNonZeroVk()
    {
        // Resolve each preset's terminal key the same way ExecuteKeyboardShortcut does (single
        // char via CharToVirtualKey, else KeyNameMapper). Accumulate any that fail so the empty-
        // list assertion names them — the suite does not use NFluent's per-item .As(...).
        var unresolved = new List<string>();
        foreach (var (label, chord) in QuickActionPresets.KeyboardShortcuts)
        {
            var key = chord.Split('+')[^1].Trim();

            ushort vk = 0;
            if (key.Length == 1)
            {
                var ch = char.IsLetter(key[0]) ? char.ToLowerInvariant(key[0]) : key[0];
                (vk, _) = Win32InputSimulator.CharToVirtualKey(ch);
            }
            if (vk == 0 && KeyNameMapper.TryMap(key, out var mappedVk, out _))
                vk = mappedVk;

            if (vk == 0)
                unresolved.Add($"{label} ('{chord}')");
        }

        Check.That(unresolved).IsEmpty();
    }

    [Fact]
    public void MediaControls_CommandsAreTheCanonicalSet()
    {
        // Guards the preset table against a typo; the executor-side dispatch is checked in
        // EventCoordinatorTests (ExecuteMediaControl is private to the App layer).
        var expected = new[] { "PlayPause", "Stop", "NextTrack", "PreviousTrack", "VolumeUp", "VolumeDown", "VolumeMute" };
        var actual = QuickActionPresets.MediaControls.Select(m => m.Command).ToList();
        Check.That(actual).ContainsExactly(expected);
    }

    // Every WindowActions payload must round-trip to a WindowAction enum member, because
    // EventCoordinator.ExecuteWindowAction parses the payload with Enum.TryParse<WindowAction>.
    // A typo here would ship a preset that silently no-ops (logs a warning, does nothing).
    [Fact]
    public void WindowActions_PayloadsAllParseToWindowActionEnum()
    {
        Check.That(QuickActionPresets.WindowActions).Not.IsEmpty();

        foreach (var (_, payload) in QuickActionPresets.WindowActions)
            Check.That(Enum.TryParse<WindowAction>(payload, out _)).IsTrue();
    }

    [Fact]
    public void WindowActions_CoversEveryWindowActionMember()
    {
        // Drift guard: adding a WindowAction member without a preset would leave it unreachable
        // from the only surface that can fire it.
        var covered = QuickActionPresets.WindowActions.Select(w => w.Payload).ToArray();

        foreach (var member in Enum.GetNames<WindowAction>())
            Check.That(covered).Contains(member);
    }
}
