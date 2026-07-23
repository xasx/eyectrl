namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using System.Collections.Generic;

public partial class KeyboardViewModel : MessengerViewModelBase,
    IRecipient<ShowKeyboardMessage>,
    IRecipient<HideKeyboardMessage>
{
    private readonly EyeCtrlSettings _settings;

    // Gaze tracking state
    private long _nearestKeyEnteredTimestamp;
    private long _keyCooldownEndTimestamp;
    private int _keyDwellMs;
    private int _cooldownMs;

    private readonly IStruggleTelemetrySink? _sink;

    /// <summary>Minimum dwell fraction an abandoned key must have reached to be reported.</summary>
    private const double KeyAbandonProgressThreshold = 0.3;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isEmojiMode;

    private readonly HashSet<ModifierKey> _activeModifiers = new();

    public IReadOnlySet<ModifierKey> ActiveModifiers => new HashSet<ModifierKey>(_activeModifiers);

    public bool IsShifted => _activeModifiers.Contains(ModifierKey.Shift)
                          || _activeModifiers.Contains(ModifierKey.CapsLock);
    [ObservableProperty] private string? _nearestKeyId;
    [ObservableProperty] private double _nearestKeyProgress;
    [ObservableProperty] private string _currentInput = "";
    [ObservableProperty] private string _activeSubLayoutId = "letters";

    private PredictionOrchestrator? _orchestrator;
    private KeyboardLayout? _layout;
    private string? _previousWord;
    private string _recentText = ""; // Track recent text for smart capitalization

    public event Action<string>? KeyDwellCompleted;

    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger,
        IStruggleTelemetrySink? sink = null)
        : base(messenger)
    {
        _settings = settings;
        _sink = sink;
    }

    public void Configure(int keyDwellMs, int cooldownMs)
    {
        _keyDwellMs = keyDwellMs;
        _cooldownMs = cooldownMs;
    }

    public void Receive(ShowKeyboardMessage message) => IsVisible = true;
    public void Receive(HideKeyboardMessage message) => IsVisible = false;

    /// <summary>
    /// Nearest-key dwell tracking: finds the closest key to the gaze point and
    /// tracks dwell time directly. Eliminates dead zones between keys and avoids
    /// circular hit-area overlap issues with rectangular keyboard keys.
    /// </summary>
    public void ProcessKeyGaze(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, bool isGazeInsideWindow, long timestampUs)
    {
        if (keyScreenPositions.Count == 0 || _keyDwellMs <= 0) return;

        if (!isGazeInsideWindow)
        {
            if (NearestKeyId != null)
            {
                EmitAbandonIfNeeded(keyScreenPositions, gazePoint, timestampUs, AbandonReason.GazeLeft);
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        // Post-activation cooldown (deliberately no emission — not a struggle signal).
        if (timestampUs < _keyCooldownEndTimestamp)
        {
            if (NearestKeyId != null)
            {
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        // Find nearest key
        string? nearestId = null;
        double nearestDist = double.MaxValue;

        foreach (var (id, center) in keyScreenPositions)
        {
            double dist = gazePoint.DistanceSquaredTo(center);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }

        if (nearestId != NearestKeyId)
        {
            EmitAbandonIfNeeded(keyScreenPositions, gazePoint, timestampUs, AbandonReason.NearestChanged);
            NearestKeyProgress = 0;
            NearestKeyId = nearestId;
            _nearestKeyEnteredTimestamp = timestampUs;
        }
        else if (nearestId != null)
        {
            long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
            NearestKeyProgress = Math.Min(1.0, (double)elapsed / (_keyDwellMs * MathConstants.MicrosecondsPerMillisecond));

            if (elapsed >= _keyDwellMs * MathConstants.MicrosecondsPerMillisecond)
            {
                EmitActivation(keyScreenPositions, gazePoint, nearestId, timestampUs);
                NearestKeyProgress = 0;
                NearestKeyId = null;
                _keyCooldownEndTimestamp = timestampUs + (_cooldownMs * MathConstants.MicrosecondsPerMillisecond);
                KeyDwellCompleted?.Invoke(nearestId);
            }
        }
    }

    // Reports an abandonment of the CURRENT NearestKeyId (still set at the call point) if it had
    // accumulated at least the threshold. No-op when there is no sink or no in-progress key.
    // The gazePoint fallback for the center is unreachable in practice — key ids are stable within
    // a layout, so keyScreenPositions always contains NearestKeyId here — but keeps this total.
    private void EmitAbandonIfNeeded(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, long timestampUs, AbandonReason reason)
    {
        if (_sink is null || NearestKeyId is null) return;
        long elapsed = timestampUs - _nearestKeyEnteredTimestamp;
        double progress = Math.Min(1.0, (double)elapsed / (_keyDwellMs * MathConstants.MicrosecondsPerMillisecond));
        if (progress < KeyAbandonProgressThreshold) return;
        var center = keyScreenPositions.TryGetValue(NearestKeyId, out var c) ? c : gazePoint;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, NearestKeyId,
            OutcomeKind.Abandoned, reason, progress, gazePoint, center, 0, null));
    }

    private void EmitActivation(Dictionary<string, PhysicalPoint> keyScreenPositions,
        PhysicalPoint gazePoint, string keyId, long timestampUs)
    {
        if (_sink is null) return;
        // gazePoint fallback is unreachable in practice (keyId just matched a dictionary entry).
        var center = keyScreenPositions.TryGetValue(keyId, out var c) ? c : gazePoint;
        _sink.Report(new DwellOutcome(
            timestampUs, DwellContext.Keyboard, keyId,
            OutcomeKind.Activated, AbandonReason.None, 1.0, gazePoint, center, 0, null));
    }

    public void SetLayout(KeyboardLayout layout)
    {
        _layout = layout;
        ActiveSubLayoutId = layout.DefaultSubLayout;
    }

    public void SetPredictor(WordPredictor? predictor)
    {
        _orchestrator?.Dispose();
        _orchestrator = predictor != null ? new PredictionOrchestrator(predictor) : null;
    }

    public void SetOrchestrator(PredictionOrchestrator? orchestrator)
    {
        if (ReferenceEquals(_orchestrator, orchestrator))
            return;

        _orchestrator?.Dispose();
        _orchestrator = orchestrator;
    }

    public void ToggleModifier(ModifierKey modifier)
    {
        if (!_activeModifiers.Remove(modifier))
            _activeModifiers.Add(modifier);
        OnPropertyChanged(nameof(ActiveModifiers));
        OnPropertyChanged(nameof(IsShifted));
    }

    private void ClearOneShotModifiers()
    {
        _activeModifiers.Remove(ModifierKey.Shift);
        _activeModifiers.Remove(ModifierKey.Ctrl);
        _activeModifiers.Remove(ModifierKey.Alt);
        _activeModifiers.Remove(ModifierKey.Win);
        OnPropertyChanged(nameof(ActiveModifiers));
        OnPropertyChanged(nameof(IsShifted));
    }

    private bool HasSystemModifiers =>
        _activeModifiers.Contains(ModifierKey.Ctrl)
        || _activeModifiers.Contains(ModifierKey.Alt)
        || _activeModifiers.Contains(ModifierKey.Win);

    private IReadOnlySet<ModifierKey>? ModifiersSnapshot =>
        _activeModifiers.Count > 0 ? new HashSet<ModifierKey>(_activeModifiers) : null;

    public void ActivateKey(string output, string? shiftOutput)
    {
        var actualOutput = IsShifted && !string.IsNullOrEmpty(shiftOutput)
            ? shiftOutput
            : output;

        // System modifier combos (Ctrl+C, Alt+F4, etc.) are shortcuts, not text input
        if (!HasSystemModifiers)
        {
            CurrentInput += actualOutput;
            _orchestrator?.LearnWord(actualOutput);
        }
        Messenger.Send(new KeyActivatedMessage(actualOutput, ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleBackspace()
    {
        if (!HasSystemModifiers && CurrentInput.Length > 0)
            CurrentInput = CurrentInput[..^1];
        else if (!HasSystemModifiers && CurrentInput.Length == 0 && _recentText.Length > 0)
        {
            // User is deleting previously committed text, update _recentText
            _recentText = _recentText[..^1];
        }
        Messenger.Send(new KeyActivatedMessage("\b", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleEnter()
    {
        if (!HasSystemModifiers)
        {
            CurrentInput = "";
            _previousWord = null;
            _recentText = ""; // Clear on new paragraph/line
        }
        Messenger.Send(new KeyActivatedMessage("\n", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleSpace()
    {
        if (!HasSystemModifiers)
        {
            if (CurrentInput.Length > 0)
            {
                _orchestrator?.LearnWord(CurrentInput);
                _previousWord = CurrentInput;
                AppendRecentText(CurrentInput);
            }
            CurrentInput = "";
        }
        Messenger.Send(new KeyActivatedMessage(" ", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleTab()
    {
        Messenger.Send(new KeyActivatedMessage("\t", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void SwitchSubLayout(string subLayoutId)
    {
        if (_layout == null || !_layout.SubLayouts.ContainsKey(subLayoutId)) return;
        if (ActiveSubLayoutId == subLayoutId) return;
        ActiveSubLayoutId = subLayoutId;
    }

    public List<string> GetSuggestions()
    {
        // Implement in terms of GetSuggestionsWithContext to avoid duplication
        return GetSuggestionsWithContext()
            .Select(r => r.Word)
            .ToList();
    }

    /// <summary>
    /// Gets prediction suggestions with full context metadata for UI display.
    /// Returns PredictionResult objects containing type, confidence, and scoring information.
    /// </summary>
    public List<PredictionResult> GetSuggestionsWithContext()
    {
        if (_orchestrator == null) return new List<PredictionResult>();

        // Pass full text context for smart capitalization if enabled
        var fullText = _settings.Keyboard.SmartCapitalizationEnabled ? _recentText : null;

        return _orchestrator.GetSuggestions(CurrentInput, _previousWord, maxCount: 5, fullText: fullText);
    }

    public void HandlePredictionSelected(string word)
    {
        _orchestrator?.LearnWord(word);
        var deleteCount = CurrentInput.Length;
        CurrentInput = "";
        _previousWord = word;
        AppendRecentText(word);
        Messenger.Send(new PredictionSelectedMessage(word, deleteCount));
    }

    public void HandleSpecialKey(string keyName)
    {
        Messenger.Send(new SpecialKeyMessage(keyName, ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    /// <summary>
    /// Appends text to recent text buffer for capitalization context, maintaining ~100 char limit.
    /// </summary>
    private void AppendRecentText(string text)
    {
        _recentText += text + " ";
        if (_recentText.Length > 100)
            _recentText = _recentText[^100..];
    }

    protected override void OnDispose()
    {
        _orchestrator?.Dispose();
    }
}
