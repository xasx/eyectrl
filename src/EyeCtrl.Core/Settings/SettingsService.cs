namespace EyeCtrl.Core.Settings;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Service for persisting and loading application settings as JSON.
/// Performs validation before saving and loads damaged files with per-setting tolerance
/// (issue #153): a defect costs the offending property or, at worst, its section — never
/// the whole file. Whole-file defaults remain only when there is no data to salvage
/// (missing/unreadable/unparseable file).
/// </summary>
/// <remarks>
/// Single-writer ownership (spec, "Lost updates, not just corruption"): each instance is constructed
/// with a <see cref="SettingsWriter"/> identifying the owning process. <see cref="Save"/> re-reads
/// the file on disk under a cross-process mutex and preserves any section the caller doesn't own,
/// then atomic-writes via temp+rename. This prevents the App process from clobbering the Supervisor's
/// <see cref="SupervisionSettings"/> (and vice versa) when both share one settings file.
/// </remarks>
public class SettingsService
{
    private readonly string _filePath;
    private readonly ILogger<SettingsService>? _logger;
    private readonly SettingsWriter _writer;
    private readonly string _writerMutexName;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>JSON property name (camelCase) of the Supervision section — the only section
    /// whose ownership is split across writers today.</summary>
    private const string SupervisionSectionName = "supervision";

    /// <summary>JSON property name of the one Supervision key the App may write through
    /// <see cref="SaveSupervisionEnabled"/>.</summary>
    private const string SupervisionEnabledKey = "enabled";

    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();
    private const int DebounceMs = 500;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class.
    /// </summary>
    /// <param name="filePath">Full path to the settings JSON file.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    /// <remarks>Equivalent to passing <see cref="SettingsWriter.App"/>: the app is the only
    /// existing writer today, so the historical default preserves backward compatibility.</remarks>
    public SettingsService(string filePath, ILogger<SettingsService>? logger = null)
        : this(filePath, SettingsWriter.App, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit writer identity for single-writer ownership.
    /// </summary>
    /// <param name="filePath">Full path to the settings JSON file.</param>
    /// <param name="writer">Which process owns this instance — controls which sections <see cref="Save"/> may persist.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    public SettingsService(string filePath, SettingsWriter writer, ILogger<SettingsService>? logger = null)
        : this(filePath, writer, WriterMutexName, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit writer mutex name.
    /// </summary>
    /// <param name="filePath">Full path to the settings JSON file.</param>
    /// <param name="writer">Which process owns this instance.</param>
    /// <param name="writerMutexName">The writer mutex to take; the production name by default.</param>
    /// <param name="logger">Optional logger for diagnostic messages.</param>
    /// <remarks>
    /// The test seam: tests that persist use a GUID-suffixed name so they never contend with a
    /// live EyeCtrl on this box (the box-wide rule for named kernel objects). Production uses the
    /// default.
    /// </remarks>
    internal SettingsService(string filePath, SettingsWriter writer, string writerMutexName, ILogger<SettingsService>? logger = null)
    {
        _filePath = filePath;
        _writer = writer;
        _writerMutexName = writerMutexName;
        _logger = logger;
    }

    /// <summary>
    /// Saves settings to disk after a debounce delay. Repeated calls within <see cref="DebounceMs"/>
    /// cancel previous pending saves, ensuring only the latest state is written.
    /// </summary>
    /// <param name="settings">The settings object to persist.</param>
    public void SaveDebounced(EyeCtrlSettings settings)
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            var oldCts = _debounceCts;
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;

            oldCts?.Cancel();
            oldCts?.Dispose();
        }

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, token);
                if (!token.IsCancellationRequested)
                    Save(settings);
            }
            catch (TaskCanceledException)
            {
                // Expected when debounce is superseded by a new save request
            }
            catch (InvalidOperationException ex)
            {
                // Settings validation failed - log but don't crash
                _logger?.LogError(ex, "Failed to save settings due to validation errors");
            }
            catch (Exception ex)
            {
                // IO or serialization errors - log but don't crash
                _logger?.LogError(ex, "Failed to save settings to {FilePath}", _filePath);
            }
        });
    }

    /// <summary>
    /// Loads settings from disk with per-setting tolerance (issue #153).
    /// </summary>
    /// <returns>
    /// The loaded settings. A damaged value costs that property (dropped to its default) or, when
    /// its section has several defects, that section — never the whole file. Whole-file defaults
    /// are returned only when there is no data to salvage: the file is missing, unreadable, or
    /// unparseable.
    /// </returns>
    /// <remarks>
    /// Design ledger (owner-vetoable; alternatives were considered and rejected as noted):
    /// <list type="bullet">
    /// <item><b>D1 — the Clamp family stays.</b> Clamps preserve the user's intent where a range
    /// is known (a mistyped 250 clamps to 200 rather than dropping to the default). The general
    /// repair below is the safety net around them, not a replacement: retiring the clamps would
    /// trade intent-preserving recovery for property drops.</item>
    /// <item><b>D2 — section acceptance is judged standalone, at the JSON-node level.</b> A section
    /// is acceptable iff it deserializes without throwing (setter guards included) AND, after the
    /// read-time clamps, passes its own <c>Validate()</c>. The root <c>Validate()</c> returns flat
    /// strings with no property attribution; restructuring all 21 implementations to return
    /// structured results was rejected as a far larger refactor with Save-message churn for no
    /// additional recovery. Node-level evaluation buys the attribution for free.</item>
    /// <item><b>D3 — single-fault repair.</b> A section failing D2 is retried with each single
    /// property dropped; the first accepting candidate wins (that property reverts to its default,
    /// the section's other values survive). Two or more faults in one section revert the section
    /// to defaults. Full multi-drop search was rejected: unbounded, and which of several bad
    /// values to keep has no principled answer. The overwhelmingly common real damage is one
    /// hand-edited typo. Known cost (#153 review F4): a section-level reset also reverts that
    /// section's once-only migration flags (e.g. ActionMenu's), so the next launch re-runs those
    /// migrations for that section — the same re-add risk the pre-#153 wipe imposed on the whole
    /// file, now scoped to one section.</item>
    /// <item><b>D4 — root scalar properties</b> (derived by reflection: every public writable
    /// non-section property — today viewPosition and selectionMode; a new one joins
    /// automatically, made visible by the enumeration pin test) are probed with the scalar
    /// analogue of D2: deserialize, plus a defined-value check for enums (the string-enum
    /// converter accepts any integer). Scalars have no <c>Validate()</c> of their own.</item>
    /// <item><b>D5 — whole-file defaults only for the no-data classes:</b> missing file, unreadable
    /// file (IO), unparseable JSON, non-object root, and a residual whole-graph failure after
    /// repair (defensive; every surviving node was proven deserializable standalone). Duplicate
    /// keys are scoped by where they sit (round-2 finding 6): at the ROOT the repair's JsonNode
    /// lookup refuses the ambiguous access (ArgumentException) and the catch answers with
    /// whole-file defaults; INSIDE a section the same refusal reads as a multi-fault verdict, so
    /// only that section defaults (TryDropOneAccept guard) and the Save-time supervision heal
    /// falls back to the incoming section instead of throwing. (Save's raw-baseline read has the
    /// same pre-existing ROOT-scope duplicate-key exposure — recorded as follow-up, unchanged
    /// here.) With no parsed data there is nothing to preserve, so the catch-all's behavior is
    /// unchanged in kind.</item>
    /// <item><b>D6 — repairs are in-memory only.</b> Unlike the lossless migrations above (which
    /// write back), repairs are lossy; persisting user-data loss at load time, before any user
    /// visibility, would amplify a repair bug's blast radius from this session's view to the file
    /// itself. The repaired state reaches disk on the next organic Save — the same path by which
    /// the old whole-file defaults reached it. One carve-out found in review (F1): the App
    /// writer's raw supervision overlay would have preserved a damaged on-disk supervision
    /// section forever (the Supervisor loads but never saves), so that overlay now heals a
    /// damaged section with the on-disk values the same drop-one verdict produces (valid
    /// siblings survive, the healed default is written explicitly); only a multi-fault section
    /// falls back to the incoming snapshot. A valid on-disk section still always wins — a
    /// Supervisor write is always valid: its Save validates first.</item>
    /// </list>
    /// </remarks>
    public EyeCtrlSettings Load()
    {
        if (!File.Exists(_filePath)) return new EyeCtrlSettings();

        try
        {
            var json = File.ReadAllText(_filePath);
            var migratedJson = ClampKeyboardDwellFloor(
                MigrateCursorFilterUnits(MigrateDragActions(json)));
            if (!ReferenceEquals(json, migratedJson))
            {
                // Atomic, like Save: this is a write during startup, and a torn one would leave the
                // user's whole settings file unparseable — which Load answers with all-defaults, so
                // every configured value would silently revert.
                try
                {
                    var tmp = _filePath + ".tmp";
                    File.WriteAllText(tmp, migratedJson);
                    File.Move(tmp, _filePath, overwrite: true);
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "Failed to persist migrated settings to {FilePath}", _filePath); }
            }

            // Fast path — the intact file (the overwhelming case): one deserialize plus the same
            // clamp/validate work Load has always done. The clamp family runs before validation
            // so the hand-editable values it protects are judged post-clamp. The scalar check
            // matters here, not only in the repair: an undefined enum INTEGER deserializes
            // "fine" (the string-enum converter accepts any int) and root Validate() covers only
            // sections — without this guard, "viewPosition": 999 would return from the fast
            // path before the tolerant path ever sees it (#153 review F3). The SelectionMode
            // seed runs only PAST validation: an empty root Validate guarantees no null section
            // (a null section is itself a validation error), and the seed dereferences
            // ManualSelection — that ordering is what lets a "manualSelection": null file take
            // the tolerant path instead of NRE-ing here (pinned by
            // Load_NullManualSelectionSection_DoesNotThrow_SiblingsSurvive).
            var settings = TryDeserializeWholeGraph(migratedJson);
            if (settings is not null)
            {
                ApplyLoadClamps(settings);
                if (settings.Validate().Count == 0 && RootScalarsAreDefined(settings))
                {
                    SeedSelectionMode(settings);
                    return settings;
                }
            }

            // Tolerant path — something in the file is damaged. Instead of the issue-#153
            // whole-file wipe, repair per setting (ledger D2-D4). Unparseable JSON throws from
            // JsonNode.Parse into the catch below: no parsed data, nothing to preserve (D5).
            settings = LoadWithSectionRepair(migratedJson);
            SeedSelectionMode(settings);
            ApplyLoadClamps(settings);

            // Belt and braces: every surviving section was accepted standalone (deserialize +
            // clamp + validate), so this is reachable only if a DEFAULTS object fails its own
            // Validate — a code bug pinned impossible by Validate_DefaultInstance_HasNoErrors.
            // Fail safe, and loudly.
            var residualErrors = settings.Validate();
            if (residualErrors.Count > 0)
            {
                _logger?.LogError("Settings still invalid after section repair of {FilePath}: {Errors}",
                    _filePath, string.Join("; ", residualErrors));
                return new EyeCtrlSettings();
            }

            return settings;
        }
        catch (Exception ex)
        {
            // D5: missing/unreadable file, unparseable JSON, non-object root, or a residual
            // whole-graph failure after repair — nothing salvageable.
            _logger?.LogError(ex, "Error loading settings from {FilePath}", _filePath);
            return new EyeCtrlSettings();
        }
    }

    private EyeCtrlSettings? TryDeserializeWholeGraph(string json)
    {
        try { return JsonSerializer.Deserialize<EyeCtrlSettings>(json, JsonOptions); }
        catch { return null; }
    }

    // Migration: seed SelectionMode from deprecated ManualSelection.Enabled
    private static void SeedSelectionMode(EyeCtrlSettings settings)
    {
        if (settings.SelectionMode == SelectionMode.None && settings.ManualSelection.Enabled)
            settings.SelectionMode = SelectionMode.ManualSelection;
    }

    // The gaze-indicator visuals were unvalidated before they got ranges (2026-08-29); a
    // hand-edited file holding an out-of-range value must clamp, not trip a rejection — the
    // file-wipe path punished exactly the hand-editors the UI serves. This dispatch point is
    // shared by the fast path, the section-acceptance probe, and the post-repair pass, so a
    // section is always judged exactly as it will be returned (a clampable value is clamped,
    // never dropped, even inside the repair).
    private void ApplyLoadClamps(EyeCtrlSettings settings)
    {
        foreach (var (_, section) in settings.EnumerateSubSettings())
            ClampSection(section);
    }

    private void ClampSection(object? section)
    {
        switch (section)
        {
            case ManualSelectionSettings ms:
                ClampGazeIndicator(ms);
                ClampManualSelectionTargeting(ms);
                break;
            case CursorSettings cursor:
                ClampCursorFilter(cursor);
                break;
            case KeyboardSettings keyboard:
                SanitizeLanguagePack(keyboard);
                ClampMaxSuggestions(keyboard);
                break;
        }
    }

    // ── Per-setting tolerance (issue #153) ───────────────────────────────────────────────────────
    //
    // The repair pass re-anchors on JsonNode.Parse: if the file parses, ANY later failure — a
    // setter-guard throw during deserialization, a JsonException from a bad enum string or a
    // type mismatch, a Validate() rejection — is attributable to a node and therefore
    // recoverable per setting. Each section is probed standalone; a failing section gets the
    // single-fault drop-one retry (D3), then defaults; the root enum scalars are probed and
    // dropped the same way (D4). The repaired tree is deserialized once more as a whole graph.

    // D4 (#153 review F2): derived by reflection, never hand-maintained — every public
    // instance writable root property that is not a section (IValidatable). A future root
    // scalar joins the probe set automatically; two pin tests make the addition and any drift
    // from this filter visible (NonSectionWritableProperties_AreExactlyTheRootScalars,
    // RootScalarRepairSet_IsDerivedByReflection_NotHandMaintained). Internal for the latter.
    internal static readonly (string Name, Type Type)[] RootScalarProperties =
    [
        .. typeof(EyeCtrlSettings).GetProperties()
            .Where(p => p.GetSetMethod() is { IsStatic: false }
                && p.GetIndexParameters().Length == 0
                && !typeof(IValidatable).IsAssignableFrom(p.PropertyType))
            .Select(p => (p.Name, p.PropertyType)),
    ];

    /// <summary>The scalar analogue of a range check for enums: an integer the string-enum
    /// converter accepted that names no defined member. [Flags] enums are exempt —
    /// <see cref="Enum.IsDefined"/> rejects legitimate combinations (A|B), and both root
    /// scalars are plain today, so a future [Flags] root scalar must not be silently
    /// defaulted by this gate (round-2 finding 8).</summary>
    internal static bool IsUndefinedEnumValue(Type type, object? value) =>
        type.IsEnum
        && !type.IsDefined(typeof(FlagsAttribute), inherit: false)
        && !Enum.IsDefined(type, value!);

    // The enum members of the scalar set, for the fast path's defined-value gate (F3): the
    // repair probe re-checks deserialized values, but an undefined enum integer never throws
    // and never reaches the repair unless the fast path refuses it first.
    private static readonly System.Reflection.PropertyInfo[] RootEnumScalarProperties =
    [
        .. RootScalarProperties
            .Where(t => t.Type.IsEnum)
            .Select(t => typeof(EyeCtrlSettings).GetProperty(t.Name)!),
    ];

    private static bool RootScalarsAreDefined(EyeCtrlSettings settings)
    {
        foreach (var property in RootEnumScalarProperties)
            if (IsUndefinedEnumValue(property.PropertyType, property.GetValue(settings)))
                return false;
        return true;
    }

    private EyeCtrlSettings LoadWithSectionRepair(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
            throw new JsonException("Settings file root is not a JSON object.");

        foreach (var (clrName, sectionType) in SectionTypes())
        {
            var key = ResolveKey(root, clrName);
            if (key is null) continue;   // absent: the fresh default instance applies
            if (root[key] is not JsonObject section)
            {
                // A null literal, scalar, or array where a section object belongs.
                root.Remove(key);
                _logger?.LogWarning("Settings section '{Section}' in {FilePath} is not an object; reset to defaults.",
                    clrName, _filePath);
                continue;
            }

            if (SectionAccepts(section, sectionType)) continue;

            if (TryDropOneAccept(section, sectionType, out var repaired, out var dropped))
            {
                root[key] = repaired;
                _logger?.LogWarning("Invalid setting '{Section}.{Property}' in {FilePath} dropped; it reverts to its default and the rest of the file loads.",
                    clrName, dropped, _filePath);
            }
            else
            {
                root.Remove(key);
                _logger?.LogWarning("Settings section '{Section}' in {FilePath} has multiple invalid values; the section reverts to defaults and the rest of the file loads.",
                    clrName, _filePath);
            }
        }

        foreach (var (name, type) in RootScalarProperties)
        {
            var key = ResolveKey(root, name);
            if (key is null) continue;
            if (root[key] is not JsonNode)   // null literal
            {
                root.Remove(key);
                continue;
            }
            try
            {
                var value = root[key]!.Deserialize(type, JsonOptions);
                // Scalars have no Validate(); for enums, defined-ness is the scalar analogue —
                // JsonStringEnumConverter accepts any integer, so "viewPosition": 999 would
                // otherwise survive as an undefined enum value (#153 review F3). [Flags]
                // combinations are exempt via IsUndefinedEnumValue (round-2 finding 8).
                if (value is null || IsUndefinedEnumValue(type, value))
                    throw new InvalidOperationException($"Invalid value for root setting '{name}'.");
            }
            catch (Exception ex)
            {
                root.Remove(key);
                _logger?.LogWarning(ex, "Invalid setting '{Property}' in {FilePath} dropped; it reverts to its default.",
                    name, _filePath);
            }
        }

        return root.Deserialize<EyeCtrlSettings>(JsonOptions) ?? new EyeCtrlSettings();
    }

    /// <summary>The section map for the repair pass: every sub-settings property from
    /// <see cref="EyeCtrlSettings.EnumerateSubSettings"/> (the drift-guarded single source of
    /// truth) paired with its declared property type.</summary>
    private static IEnumerable<(string ClrName, Type SectionType)> SectionTypes() =>
        new EyeCtrlSettings().EnumerateSubSettings()
            .Select(t => (t.Name, typeof(EyeCtrlSettings).GetProperty(t.Name)!.PropertyType));

    /// <summary>D2: a section is acceptable iff it deserializes without throwing (setter guards
    /// included) AND, after the read-time clamps, passes its own Validate(). This variant also
    /// hands back the accepted (clamped) instance, for callers that need the repaired VALUES —
    /// a minus-one-key JSON node is sparse, and "dropped to default" must read back as an
    /// explicit default where the result is written to disk.</summary>
    private bool TryAcceptSection(JsonObject section, Type sectionType,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? accepted)
    {
        try
        {
            if (section.Deserialize(sectionType, JsonOptions) is not { } candidate)
            {
                accepted = null;
                return false;
            }
            ClampSection(candidate);
            if (candidate is IValidatable validatable && validatable.Validate().Count > 0)
            {
                accepted = null;
                return false;
            }
            accepted = candidate;
            return true;
        }
        catch (Exception)
        {
            accepted = null;
            return false;
        }
    }

    private bool SectionAccepts(JsonObject section, Type sectionType) =>
        TryAcceptSection(section, sectionType, out _);

    /// <summary>D3: single-fault repair — retry the section with each property dropped in turn;
    /// the first candidate that accepts wins. Returns false when no single drop fixes the
    /// section (multi-fault), leaving the caller to default the section wholesale.</summary>
    private bool TryDropOneAccept(JsonObject section, Type sectionType,
        out JsonObject repaired, out string dropped)
    {
        // A section with INNER duplicate keys makes the DOM refuse ambiguous access
        // (ArgumentException from enumeration/indexing). That refusal reads as a multi-fault
        // verdict — same as an unrepairable section — so the caller defaults the section and
        // siblings survive (round-2 finding 1; without this guard the throw escaped Load's
        // repair AND, worse, Save's overlay: every debounced settings write was silently
        // swallowed and the file could never heal).
        try
        {
            foreach (var key in section.Select(k => k.Key).ToList())
            {
                repaired = section.DeepClone().AsObject();
                repaired.Remove(key);
                if (SectionAccepts(repaired, sectionType))
                {
                    dropped = key;
                    return true;
                }
            }
        }
        catch (ArgumentException) { }
        repaired = null!;
        dropped = null!;
        return false;
    }

    /// <summary>Section keys bind exactly the way the deserializer binds them: the naming-policy
    /// name only ("dwell"). A differently-spelled key ("Dwell") is an unknown member STJ ignores
    /// — the established unknown-JSON-member contract — so the repair must not go looking for it:
    /// it can never influence the outcome. Verified empirically on net10: a PascalCase section
    /// key neither binds nor throws.</summary>
    private static string? ResolveKey(JsonObject root, string clrName) =>
        root.ContainsKey(JsonNamingPolicy.CamelCase.ConvertName(clrName))
            ? JsonNamingPolicy.CamelCase.ConvertName(clrName)
            : null;

    private void ClampGazeIndicator(ManualSelectionSettings ms)
    {
        var (outer, dot, stroke, opacity, anim) = (
            ms.GazeIndicatorOuterDiameter, ms.GazeIndicatorCenterDotDiameter,
            ms.GazeIndicatorStrokeThickness, ms.GazeIndicatorOpacity,
            ms.GazeIndicatorResetAnimationMs);
        ms.GazeIndicatorOuterDiameter = Math.Clamp(outer, 20, 200);
        ms.GazeIndicatorCenterDotDiameter = Math.Clamp(dot, 2, 40);
        ms.GazeIndicatorStrokeThickness = Math.Clamp(stroke, 0.5, 10);
        ms.GazeIndicatorOpacity = Math.Clamp(opacity, 0.1, 1.0);
        ms.GazeIndicatorResetAnimationMs = Math.Clamp(anim, 0, 1000);
        if (ms.GazeIndicatorOuterDiameter != outer || ms.GazeIndicatorCenterDotDiameter != dot
            || ms.GazeIndicatorStrokeThickness != stroke || ms.GazeIndicatorOpacity != opacity
            || ms.GazeIndicatorResetAnimationMs != anim)
        {
            _logger?.LogInformation("Clamped out-of-range gaze-indicator settings to their valid ranges.");
        }
    }

    // The commit-rule settings landed unguarded-by-setter, like the gaze-indicator visuals, so a
    // hand-edited file must clamp rather than trip the whole-file default. StabilityWindowSize is
    // additionally lifted to StabilitySampleCount: K > M would make stability onset impossible and
    // manual selection would silently stop activating.
    private void ClampManualSelectionTargeting(ManualSelectionSettings ms)
    {
        var (hold, k, m, skip) = (
            ms.HoldRadiusLogical, ms.StabilitySampleCount, ms.StabilityWindowSize, ms.HoldOutlierSkipLimit);
        ms.HoldRadiusLogical = Math.Clamp(hold, 20, 300);
        ms.StabilitySampleCount = Math.Clamp(k, 2, 30);
        ms.StabilityWindowSize = Math.Clamp(Math.Max(m, ms.StabilitySampleCount), 2, 60);
        ms.HoldOutlierSkipLimit = Math.Clamp(skip, 0, 30);
        if (ms.HoldRadiusLogical != hold || ms.StabilitySampleCount != k
            || ms.StabilityWindowSize != m || ms.HoldOutlierSkipLimit != skip)
        {
            _logger?.LogInformation("Clamped out-of-range manual-selection targeting settings to their valid ranges.");
        }
    }

    // The Stage 3 deadband knobs have no setter guard and no Validate() entry on purpose (see their XML
    // docs), so this is the ONLY thing standing between a hand-edited value and the stabiliser. Clamping
    // rather than rejecting is the whole design: Load answers a validation error by discarding the user's
    // entire settings file, and a brand-new setting is the likeliest one to be typed wrong.
    private void ClampCursorFilter(CursorSettings cursor)
    {
        var (radius, confirm) = (cursor.DeadbandRadiusPx, cursor.DeadbandConfirmSamples);
        cursor.DeadbandRadiusPx = Math.Clamp(radius, 0.0, 15.0);
        cursor.DeadbandConfirmSamples = Math.Clamp(confirm, 1, 5);
        if (cursor.DeadbandRadiusPx != radius || cursor.DeadbandConfirmSamples != confirm)
            _logger?.LogInformation("Clamped out-of-range cursor deadband settings to their valid ranges.");
    }

    // Same bargain as the neighbours above: MaxSuggestions is a brand-new hand-editable int and
    // the likeliest single value to be typed wrong; an out-of-range one must cost one wrong count,
    // not the user's entire settings file.
    private void ClampMaxSuggestions(KeyboardSettings keyboard)
    {
        var count = keyboard.MaxSuggestions;
        keyboard.MaxSuggestions = Math.Clamp(count, 1, KeyboardSettings.MaxSuggestionsCeiling);
        if (keyboard.MaxSuggestions != count)
            _logger?.LogInformation("Clamped out-of-range MaxSuggestions to the 1-{Max} range.",
                KeyboardSettings.MaxSuggestionsCeiling);
    }

    // LanguagePack is a path ingredient (dictionary/trie reads, user-dictionary write), so a
    // hand-edited "..\..\evil" would read and write outside the intended trees. Reset to empty
    // (auto-detect) — same clamp-not-reject design as above: a validation error would wipe the
    // whole settings file over one string.
    private void SanitizeLanguagePack(KeyboardSettings keyboard)
    {
        if (KeyboardSettings.IsValidLanguagePack(keyboard.LanguagePack)) return;
        _logger?.LogWarning("Ignoring malformed keyboard language pack '{Lang}'; falling back to auto-detection.",
            keyboard.LanguagePack);
        keyboard.LanguagePack = "";
    }

    /// <summary>
    /// Cross-process writer mutex name. Settings writers hold it across their read-merge-replace
    /// (<see cref="Save"/> and the one-key supervision persist); outside readers (the Supervisor's
    /// enabled re-read) take it too, so no read handle is open while a writer replaces the file —
    /// <c>File.Move(..., overwrite: true)</c> fails with access denied under an open handle.
    /// </summary>
    public const string WriterMutexName = @"Local\EyeCtrl.Settings.Writer";

    /// <summary>
    /// Validates and immediately saves settings to disk. Throws if validation fails.
    /// </summary>
    /// <param name="settings">The settings object to persist.</param>
    /// <exception cref="InvalidOperationException">Thrown when settings fail validation.</exception>
    /// <remarks>
    /// Single-writer ownership is enforced here: the file is re-read on disk under a cross-process
    /// mutex and sections the caller doesn't own are preserved, so a stale in-memory snapshot can
    /// never overwrite another process's section (spec, "Lost updates"). The write is atomic
    /// (temp file + rename) so a reader never sees a partially-written file.
    /// </remarks>
    public void Save(EyeCtrlSettings settings)
    {
        // Validate before saving (preserved from pre-ownership behavior).
        var validationErrors = settings.Validate();
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot save invalid settings. Errors:\n{string.Join("\n", validationErrors)}");
        }

        // Cross-process mutual exclusion so the read-merge-write sequence is atomic across writers.
        using var mutex = new Mutex(false, _writerMutexName);
        bool owned = false;
        try
        {
            mutex.WaitOne();
            owned = true;

            // Read the on-disk state RAW (not via Load()) for section preservation. Load() returns
            // all-defaults when the file fails validation, which would let this writer silently reset
            // a section it doesn't own but must preserve (spec, "Lost updates"). See MergeForWriter.
            string? onDiskRaw = null;
            if (File.Exists(_filePath))
            {
                try { onDiskRaw = File.ReadAllText(_filePath); }
                catch { onDiskRaw = null; } // unreadable -> treat as absent (can't preserve)
            }

            var mergedJson = MergeForWriter(settings, onDiskRaw);

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, mergedJson);
            File.Move(tmp, _filePath, overwrite: true);
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Persists ONLY <c>supervision.enabled</c> — the single App→Supervision ownership exception.
    /// </summary>
    /// <remarks>
    /// The App cannot persist the Supervision section (see <see cref="SettingsWriter"/>): an
    /// App-writer <see cref="Save"/> overlays the raw on-disk section, which is what stops a stale
    /// in-memory snapshot from clobbering the Supervisor's values — and is why the App's own flip
    /// of this flag was silently discarded whenever a valid section existed. This method is the
    /// sanctioned way around that: it edits the one key on the raw on-disk JSON under the same
    /// cross-process mutex as <see cref="Save"/>, preserving every other key — in the section and
    /// in every other section, unknown/future keys included — so it can never clash with the
    /// Supervisor's own edits to its other values.
    /// <para>
    /// Must be called on an instance constructed with <see cref="SettingsWriter.Supervisor"/>: the
    /// guard makes the ownership exception explicit in code rather than an accident of wiring.
    /// The App calls it, for exactly this one key; nothing else in the section is the App's to write.
    /// </para>
    /// </remarks>
    /// <param name="enabled">Whether supervised process lifecycle is on.</param>
    /// <exception cref="InvalidOperationException">
    /// This instance is not a <see cref="SettingsWriter.Supervisor"/> writer, or the on-disk file
    /// exists but cannot be read as a JSON object. Editing in place would have to discard what
    /// cannot be read, which is not this method's call to make — a whole-file write is
    /// <see cref="Save"/>'s.
    /// </exception>
    public void SaveSupervisionEnabled(bool enabled)
    {
        if (_writer != SettingsWriter.Supervisor)
        {
            throw new InvalidOperationException(
                "SaveSupervisionEnabled is the App's single write into the Supervision section and "
                + "must be called on a SettingsService constructed with SettingsWriter.Supervisor.");
        }

        // The same cross-process mutex Save uses: this is a read-edit-write of the shared file,
        // and a concurrent Supervisor save must either precede or follow it, never interleave.
        using var mutex = new Mutex(false, _writerMutexName);
        bool owned = false;
        try
        {
            mutex.WaitOne();
            owned = true;

            // Read the raw baseline: the whole point of this write is "one key changes, everything
            // else stays". An unreadable, unparseable, or non-object file has nothing safe to edit,
            // so fail instead of guessing — the file is left untouched for the caller to report.
            JsonObject root;
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                root = ParseObjectForEdit(json);
            }
            else
            {
                root = new JsonObject();
            }

            // A NON-OBJECT section (scalar, array, null) is replaced by a minimal valid one: the
            // flag cannot be written through it, and the section was unusable in its current
            // form. An OBJECT section with bad values is edited in place — the load-time repair
            // heals it on the next App save. Everything outside the section is still preserved
            // verbatim.
            if (root[SupervisionSectionName] is not JsonObject supervision)
            {
                supervision = new JsonObject();
                root[SupervisionSectionName] = supervision;
            }

            supervision[SupervisionEnabledKey] = enabled;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(JsonOptions));
            File.Move(tmp, _filePath, overwrite: true);
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    private JsonObject ParseObjectForEdit(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException(
                    $"Cannot edit supervision.enabled: {_filePath} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Cannot edit supervision.enabled: {_filePath} is not valid JSON.", ex);
        }
    }

    /// <summary>
    /// Produces the JSON to persist after applying single-writer ownership. The caller-owned
    /// section comes from <paramref name="settings"/>; every other section is preserved from the
    /// raw on-disk JSON in <paramref name="onDiskRaw"/>. Implemented at the JSON-node level so it generalizes to any
    /// future owned section without hand-copying <see cref="EyeCtrlSettings"/>'s many properties
    /// (<see cref="EyeCtrlSettings"/> is a class, not a record, so a <c>with</c> expression is
    /// unavailable).
    /// </summary>
    private string MergeForWriter(EyeCtrlSettings settings, string? onDiskRaw)
    {
        var incoming = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();

        // Preserve the not-owned section(s) from the RAW on-disk JSON — NOT from a validating Load(),
        // which substitutes all-defaults when any section fails validation and would silently reset a
        // section this writer must preserve. Parse defensively: a missing/corrupt file means we can't
        // preserve, and fall back to the incoming snapshot (or defaults).
        JsonObject? baseline = null;
        if (!string.IsNullOrWhiteSpace(onDiskRaw))
        {
            try { baseline = JsonNode.Parse(onDiskRaw) as JsonObject; }
            catch { baseline = null; }
        }

        if (_writer == SettingsWriter.Supervisor)
        {
            // Supervisor owns ONLY Supervision: start from the raw on-disk object so every App-owned
            // section is preserved verbatim (even values this schema no longer understands), then
            // overlay the incoming Supervision.
            var merged = baseline ?? JsonSerializer.SerializeToNode(new EyeCtrlSettings(), JsonOptions)!.AsObject();
            merged[SupervisionSectionName] = incoming[SupervisionSectionName]?.DeepClone();
            return merged.ToJsonString(JsonOptions);
        }

        // App owns everything EXCEPT Supervision: start from the incoming (valid) snapshot, then
        // overlay the raw on-disk Supervision so the supervisor's section survives even when the
        // file as a whole is damaged. If disk had no Supervision section, keep the incoming default.
        //
        // EXCEPT when the on-disk section is itself damaged: the Supervisor loads but never saves,
        // so a raw overlay would preserve the damage on disk forever — repaired in memory every
        // launch, never healed (#153 review F1). Then the overlay uses the on-disk section's
        // REPAIRED values (same drop-one verdict as Load), serialized whole so the healed default
        // is explicit on disk — valid siblings survive and only the damage heals. Keeping the
        // incoming section instead would discard those siblings whenever the caller saves a
        // snapshot it never loaded (Save(new EyeCtrlSettings()), the pattern the writer-ownership
        // tests themselves use); that fallback remains only for a multi-fault section. The
        // ownership guarantee is intact: the Supervisor's own writes are always valid (its Save
        // validates before writing), so a VALID on-disk section still always wins the overlay.
        var onDiskSupervision = baseline?[SupervisionSectionName] as JsonObject;
        if (onDiskSupervision is not null)
        {
            if (SectionAccepts(onDiskSupervision, typeof(SupervisionSettings)))
            {
                incoming[SupervisionSectionName] = onDiskSupervision.DeepClone();
            }
            else if (TryDropOneAccept(onDiskSupervision, typeof(SupervisionSettings), out var repairedNode, out var droppedKey)
                && TryAcceptSection(repairedNode, typeof(SupervisionSettings), out var repairedSupervision))
            {
                // Heal IN PLACE: overwrite every schema-known key on the raw node with its
                // accepted value (the dropped one becomes an explicit default) instead of
                // replacing the node with the serialized instance — a replacement would drop
                // unknown/future keys the valid-path raw overlay preserves verbatim (round-2
                // finding 4). The raw node is duplicate-free here: a duplicate section fails
                // TryDropOneAccept and lands in the branch below.
                var healed = onDiskSupervision.DeepClone().AsObject();
                var acceptedValues = JsonSerializer.SerializeToNode(repairedSupervision, JsonOptions)!.AsObject();
                foreach (var (key, value) in acceptedValues)
                    healed[key] = value?.DeepClone();
                incoming[SupervisionSectionName] = healed;
                _logger?.LogWarning("Invalid on-disk setting 'supervision.{Property}' in {FilePath} dropped; the section heals on this save.",
                    droppedKey, _filePath);
            }
            else
            {
                _logger?.LogWarning(
                    "On-disk settings section 'supervision' in {FilePath} has multiple invalid values; keeping the in-memory section so the damage heals on this save.",
                    _filePath);
            }
        }
        return incoming.ToJsonString(JsonOptions);
    }

    // ── Stage 3 filter retune migration ──────────────────────────────────────────────────────────
    //
    // WHY a migration is needed at all: Load deserialises straight onto the object graph, so a changed
    // default is invisible to every profile that already has the old one written out — and every profile
    // does, because Save serialises the whole object. Without this entry the retune would ship inert on
    // the one machine it was designed for.
    //
    // THE DETECTION RULE, and its two halves:
    //
    //   beta — the JSON key itself is the marker. The pixel-unit value lived under "oneEuroBeta"; the
    //   rescaled one lives under "oneEuroBetaPerDiagonal". Presence of the old key means "not yet
    //   migrated", which is durable, unambiguous and idempotent — unlike a value test, which would
    //   re-migrate a user who later dials the new beta down into the old key's range.
    //
    //   minCutoff — no key change is possible (the units are still Hz), so the rule is value-based:
    //   exactly the old default 5.0 means "never touched", anything else is the user's and is kept. It
    //   rides the beta key's marker, so it also runs exactly once. The known cost is a user who
    //   deliberately chose 5.0 and gets moved to 1.5; that is the trade the ticket asks for, and 5.0 was
    //   a bad value for everyone on this hardware.
    //
    // A user-changed beta is CONVERTED, not kept: the units changed underneath it, so preserving the
    // number would silently change the behaviour, while preserving the behaviour is what "preserve the
    // value the user chose" has to mean. The conversion is clamped into the new validated range here,
    // because a persisted out-of-range value makes the [ObservableProperty] setter throw during
    // deserialisation — which Load answers by discarding the whole file.
    private const string LegacyBetaKey = "oneEuroBeta";
    private const string BetaKey = "oneEuroBetaPerDiagonal";
    private const string MinCutoffKey = "oneEuroMinCutoff";
    private const string CursorSectionName = "cursor";
    // Both are exactly representable as doubles, so == against a JSON round-trip is safe.
    private const double LegacyDefaultBeta = 0.5;
    private const double LegacyDefaultMinCutoff = 5.0;

    private static string MigrateCursorFilterUnits(string json)
    {
        // Cheap reject. "oneEuroBetaPerDiagonal" does not contain the quoted old key, so an
        // already-migrated file never reaches the parse.
        if (!json.Contains($"\"{LegacyBetaKey}\"", StringComparison.Ordinal))
            return json;

        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return json; }   // unparseable: leave it to Load's own error path

        if (root?[CursorSectionName] is not JsonObject cursor)
            return json;
        if (!cursor.Remove(LegacyBetaKey, out var legacy) || legacy is not JsonValue legacyValue)
            return json;

        // A file carrying BOTH keys has already been migrated and then hand-edited (or written by a
        // mixed-version pair of processes). Drop the stale key and keep the new value untouched.
        if (cursor[BetaKey] is null && legacyValue.TryGetValue(out double betaPixelUnits))
        {
            var defaults = new CursorSettings();
            // A legal JSON number can overflow a double: "1e400" parses fine and TryGetValue hands back
            // positive Infinity (verified, not assumed). Math.Clamp would turn that into 60 — the
            // MAXIMUM, i.e. the least smoothing on the dial — which is a strange thing to hand someone
            // whose file was garbage. The default is the better answer, and it is also the only branch
            // that would save a NaN, which Math.Clamp passes through untouched and the setter's range
            // guard cannot reject (every comparison against NaN is false).
            cursor[BetaKey] = betaPixelUnits == LegacyDefaultBeta || !double.IsFinite(betaPixelUnits)
                ? defaults.OneEuroBetaPerDiagonal
                : Math.Clamp(betaPixelUnits * Processing.OneEuroFilter.SpeedNormalizationPixels, 0.0, 60.0);

            if (cursor[MinCutoffKey] is JsonValue cutoffValue
                && cutoffValue.TryGetValue(out double minCutoff)
                && minCutoff == LegacyDefaultMinCutoff)
            {
                cursor[MinCutoffKey] = defaults.OneEuroMinCutoff;
            }
        }

        return root.ToJsonString(JsonOptions);
    }

    // ── Keyboard dwell floor clamp ───────────────────────────────────────────────────────────────
    //
    // WHY raw JSON and not the deserialized object: KeyboardKeyDwellMs / KeyboardPredictionDwellMs
    // are [ObservableProperty] setters whose guards now floor at
    // AdaptationDwellRanges.KeyboardMinDwellMs (300). That floor closes the SINGLE-stall case (a
    // dwell can no longer be completed by one credited ≤150 ms silence — the per-gap multi-stall
    // qualifier lives on DwellDetector.MaxCreditedSampleGapUs) — but a file written before the
    // floor existed, or hand-edited below it, makes the setter throw DURING deserialisation, and
    // Load answers a setter throw with a brand-new EyeCtrlSettings: the user loses every configured
    // value over one number that was legal when it was written. Clamping at the JSON node, before
    // the deserialiser runs, is the only point that can keep the rest of the file.
    //
    // Clamp, not reject, is the design already used for hand-editable unguarded values
    // (ClampGazeIndicator / ClampManualSelectionTargeting / ClampCursorFilter / SanitizeLanguagePack):
    // the file-wipe path punishes exactly the hand-editors the UI serves, and this profile
    // demonstrably hand-edits it (three settings.json.bak-* files on record). Values above the
    // guard ceiling clamp too — a dwell past 1500 would wait past every other surface in the app,
    // and the ceiling is the guard's own number.
    private const string DwellSectionName = "dwell";
    private const string KeyboardKeyDwellMsKey = "keyboardKeyDwellMs";
    private const string KeyboardPredictionDwellMsKey = "keyboardPredictionDwellMs";

    private static string ClampKeyboardDwellFloor(string json)
    {
        // Cheap reject: neither key present means nothing to clamp (the common already-legal file).
        if (!json.Contains($"\"{KeyboardKeyDwellMsKey}\"", StringComparison.Ordinal)
            && !json.Contains($"\"{KeyboardPredictionDwellMsKey}\"", StringComparison.Ordinal))
            return json;

        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return json; }   // unparseable: leave it to Load's own error path

        if (root?[DwellSectionName] is not JsonObject dwell)
            return json;

        bool clampedKey = ClampDwellField(dwell, KeyboardKeyDwellMsKey);
        bool clampedPrediction = ClampDwellField(dwell, KeyboardPredictionDwellMsKey);
        return clampedKey || clampedPrediction ? root.ToJsonString(JsonOptions) : json;
    }

    private static bool ClampDwellField(JsonObject dwell, string key)
    {
        if (dwell[key] is not JsonValue node
            || !node.TryGetValue(out int value)
            || (value >= AdaptationDwellRanges.KeyboardMinDwellMs && value <= AdaptationDwellRanges.KeyboardMaxDwellMs))
        {
            return false;
        }

        dwell[key] = Math.Clamp(value, AdaptationDwellRanges.KeyboardMinDwellMs,
            AdaptationDwellRanges.KeyboardMaxDwellMs);
        return true;
    }

    private static string MigrateDragActions(string json)
    {
        if (!json.Contains("\"dragStart\"", StringComparison.OrdinalIgnoreCase) &&
            !json.Contains("\"dragEnd\"", StringComparison.OrdinalIgnoreCase))
            return json;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("actionMenu", out var actionMenu) ||
            !actionMenu.TryGetProperty("visibleActions", out var visibleActions))
            return json;

        var actions = new List<string>();
        bool modified = false;

        foreach (var item in visibleActions.EnumerateArray())
        {
            var value = item.GetString()!;
            if (string.Equals(value, "dragStart", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add("drag");
                modified = true;
            }
            else if (string.Equals(value, "dragEnd", StringComparison.OrdinalIgnoreCase))
            {
                modified = true;
            }
            else
            {
                actions.Add(value);
            }
        }

        if (!modified)
            return json;

        if (!actions.Contains("drag"))
            actions.Add("drag");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "actionMenu")
                {
                    writer.WritePropertyName("actionMenu");
                    writer.WriteStartObject();
                    foreach (var amProp in prop.Value.EnumerateObject())
                    {
                        if (amProp.Name == "visibleActions")
                        {
                            writer.WritePropertyName("visibleActions");
                            writer.WriteStartArray();
                            foreach (var a in actions)
                                writer.WriteStringValue(a);
                            writer.WriteEndArray();
                        }
                        else
                        {
                            amProp.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
