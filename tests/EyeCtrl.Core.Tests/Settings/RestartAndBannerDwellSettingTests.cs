namespace EyeCtrl.Core.Tests.Settings;

using System.Text.Json.Nodes;
using EyeCtrl.Core.Settings;

/// <summary>
/// B6's two NEW knobs, <see cref="DwellSettings.RestartNoticeDwellMs"/> and
/// <see cref="DwellSettings.EyeCareBannerDwellMs"/>: both default 0 = follow
/// <see cref="DwellSettings.ActivationButtonDwellMs"/> live, and a value above zero is 200–3000.
/// A new property means an existing settings file picks the default up from the initializer; no
/// existing default changes.
/// </summary>
public class RestartAndBannerDwellSettingTests
{
    [Fact]
    public void BothKnobs_DefaultToZero_AndFollowTheActivationDwell()
    {
        var dwell = new DwellSettings();

        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(0);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(0);
        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Fact]
    public void AZeroKnob_FollowsTheActivationDwellLive()
    {
        var dwell = new DwellSettings();
        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(700);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(700);

        dwell.ActivationButtonDwellMs = 900;

        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(900);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(900);
    }

    [Fact]
    public void APositiveKnob_WinsOverTheActivationDwell_AndDoesNotFollowIt()
    {
        var dwell = new DwellSettings
        {
            RestartNoticeDwellMs = 1200,
            EyeCareBannerDwellMs = 1400,
            ActivationButtonDwellMs = 900,
        };

        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(1200);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(1400);

        dwell.ActivationButtonDwellMs = 500;

        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(1200);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(1400);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(3000)]
    public void BothKnobs_AcceptZeroAndTheRangeEnds(int value)
    {
        var dwell = new DwellSettings
        {
            RestartNoticeDwellMs = value,
            EyeCareBannerDwellMs = value,
        };

        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(value);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(value);
        Check.That(dwell.Validate()).IsEmpty();
    }

    /// <summary>
    /// The stepper's real path back to "follow the activation dwell": from a positive value down
    /// to 0. The object-initializer case above cannot prove the guard accepts 0 — the generated
    /// setter short-circuits when the new value equals the field (and the default IS 0) — so a
    /// guard that rejects 0 would still pass it. This is the mutation-proof half.
    /// </summary>
    [Fact]
    public void BothKnobs_ReturnToZero_FromAPositiveValue()
    {
        var dwell = new DwellSettings
        {
            RestartNoticeDwellMs = 500,
            EyeCareBannerDwellMs = 600,
        };

        dwell.RestartNoticeDwellMs = 0;
        dwell.EyeCareBannerDwellMs = 0;

        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(0);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(0);
        Check.That(dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(dwell.ActivationButtonDwellMs);
        Check.That(dwell.Validate()).IsEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(199)]
    [InlineData(3001)]
    public void BothKnobs_RejectValuesOutsideZeroAndTheRange(int value)
    {
        var dwell = new DwellSettings();

        Check.ThatCode(() => dwell.RestartNoticeDwellMs = value).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => dwell.EyeCareBannerDwellMs = value).Throws<ArgumentOutOfRangeException>();
        Check.That(dwell.RestartNoticeDwellMs).IsEqualTo(0);
        Check.That(dwell.EyeCareBannerDwellMs).IsEqualTo(0);
    }

    [Theory]
    [InlineData("_restartNoticeDwellMs", nameof(DwellSettings.RestartNoticeDwellMs), 100)]
    [InlineData("_restartNoticeDwellMs", nameof(DwellSettings.RestartNoticeDwellMs), 3001)]
    [InlineData("_eyeCareBannerDwellMs", nameof(DwellSettings.EyeCareBannerDwellMs), 100)]
    [InlineData("_eyeCareBannerDwellMs", nameof(DwellSettings.EyeCareBannerDwellMs), 3001)]
    public void Validate_FlagsAnOutOfRangeKnob_LoadedAroundTheGuard(string field, string property, int value)
    {
        var dwell = new DwellSettings();
        typeof(DwellSettings).GetField(field,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dwell, value);

        var errors = dwell.Validate();

        // The planted knob is the only invalid value on a default instance, so a per-knob,
        // per-side case proves the 1–199 hole is rejected on BOTH knobs.
        Check.That(errors).HasSize(1);
        Check.That(errors[0]).Contains(property);
        Check.That(errors[0]).Contains("0 or between 200 and 3000");
    }

    /// <summary>
    /// F1 (review 01): the two <c>Effective*</c> getters are DERIVED, not settings. Without
    /// <c>[JsonIgnore]</c> System.Text.Json writes them into the owner's settings.json, where they
    /// look editable but are dead on load and go stale as soon as the activation dwell moves. The
    /// serialized file must never contain them; the two knobs they derive from must still be there.
    /// </summary>
    [Fact]
    public void ASave_WritesNeitherEffectiveKey_ButBothKnobs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_b6save_{Guid.NewGuid()}.json");
        try
        {
            new SettingsService(path).Save(new EyeCtrlSettings());

            var dwell = JsonNode.Parse(File.ReadAllText(path))!["dwell"]!.AsObject();

            Check.That(dwell.ContainsKey("effectiveRestartNoticeDwellMs")).IsFalse();
            Check.That(dwell.ContainsKey("effectiveEyeCareBannerDwellMs")).IsFalse();
            Check.That(dwell.ContainsKey("restartNoticeDwellMs")).IsTrue();
            Check.That(dwell.ContainsKey("eyeCareBannerDwellMs")).IsTrue();
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// F1's round-trip half, over a pre-B6 file shaped like the owner's: every value survives a
    /// load/save unchanged, and the ONLY new leaves the save adds are the two knobs. The old file
    /// starts from a fully serialized default (so it has the same shape the app writes) with the
    /// two B6 keys removed — exactly what the owner's file looked like before this branch.
    /// </summary>
    [Fact]
    public void APreB6File_RoundTripsUnchanged_AndGainsOnlyTheTwoKnobs()
    {
        var seed = Path.Combine(Path.GetTempPath(), $"eyectrl_b6seed_{Guid.NewGuid()}.json");
        var old = Path.Combine(Path.GetTempPath(), $"eyectrl_b6old_{Guid.NewGuid()}.json");
        var saved = Path.Combine(Path.GetTempPath(), $"eyectrl_b6out_{Guid.NewGuid()}.json");
        try
        {
            new SettingsService(seed).Save(new EyeCtrlSettings());
            var oldNode = JsonNode.Parse(File.ReadAllText(seed))!.AsObject();
            var oldDwell = oldNode["dwell"]!.AsObject();
            oldDwell.Remove("restartNoticeDwellMs");
            oldDwell.Remove("eyeCareBannerDwellMs");
            File.WriteAllText(old, oldNode.ToJsonString());

            var loaded = new SettingsService(old).Load();
            Check.That(loaded.Validate()).IsEmpty();
            Check.That(loaded.Dwell.RestartNoticeDwellMs).IsEqualTo(0);
            Check.That(loaded.Dwell.EyeCareBannerDwellMs).IsEqualTo(0);

            new SettingsService(saved).Save(loaded);

            var before = JsonNode.Parse(File.ReadAllText(old))!;
            var after = JsonNode.Parse(File.ReadAllText(saved))!;
            var diffs = new List<string>();
            Walk(before, after, "", diffs);
            Check.WithCustomMessage(
                    "a pre-B6 settings file did not round-trip; diffs: " + string.Join("; ", diffs))
                .That(diffs).IsEmpty();

            var added = LeafPaths(after).Except(LeafPaths(before))
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            Check.WithCustomMessage("the save added keys other than the two new knobs")
                .That(added).ContainsExactly(".dwell.eyeCareBannerDwellMs", ".dwell.restartNoticeDwellMs");
            Check.WithCustomMessage("a derived effective key reached the saved file")
                .That(LeafPaths(after).Where(p => p.Contains("ffective", StringComparison.Ordinal))).IsEmpty();
        }
        finally
        {
            File.Delete(seed);
            File.Delete(old);
            File.Delete(saved);
        }
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKeys_LoadsTheDefaults_AndKeepsEveryOtherValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_b6knobs_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """
            {
              "dwell": { "activationButtonDwellMs": 900, "resumeDwellMs": 1200 }
            }
            """);

            var loaded = new SettingsService(path).Load();

            Check.That(loaded.Dwell.RestartNoticeDwellMs).IsEqualTo(0);
            Check.That(loaded.Dwell.EyeCareBannerDwellMs).IsEqualTo(0);
            Check.That(loaded.Dwell.EffectiveRestartNoticeDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.EffectiveEyeCareBannerDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.ActivationButtonDwellMs).IsEqualTo(900);
            Check.That(loaded.Dwell.ResumeDwellMs).IsEqualTo(1200);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Every leaf the save left behind: a key path for object leaves, the array/primitive
    /// path itself for arrays and scalars. Used to name the keys a save ADDED, not to compare values.</summary>
    private static HashSet<string> LeafPaths(JsonNode? node, string at = "")
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                    paths.UnionWith(LeafPaths(value, at + "." + key));
                break;
            default:
                paths.Add(at);
                break;
        }
        return paths;
    }

    /// <summary>Walks every leaf present BEFORE the save and records any that changed or vanished.</summary>
    private static void Walk(JsonNode? before, JsonNode? after, string at, List<string> diffs)
    {
        if (before is JsonObject beforeObject)
        {
            if (after is not JsonObject afterObject)
            {
                diffs.Add($"{at}: object became {after?.ToJsonString() ?? "<absent>"}");
                return;
            }
            foreach (var (key, value) in beforeObject)
                Walk(value, afterObject[key], at + "." + key, diffs);
            return;
        }

        var expected = before?.ToJsonString() ?? "null";
        var actual = after?.ToJsonString() ?? "<absent>";
        if (expected != actual)
            diffs.Add($"{at}: {expected} -> {actual}");
    }
}
