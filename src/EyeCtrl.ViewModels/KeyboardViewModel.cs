namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using System.Collections.Generic;

public partial class KeyboardViewModel : ObservableObject,
    IRecipient<ShowKeyboardMessage>,
    IRecipient<HideKeyboardMessage>,
    IDisposable
{
    private readonly EyeCtrlSettings _settings;
    private readonly IMessenger _messenger;
    private bool _disposed;

    // Gaze tracking state
    private long _nearestKeyEnteredTimestamp;
    private long _keyCooldownEndTimestamp;
    private int _keyDwellMs;
    private int _cooldownMs;

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

    private WordPredictor? _predictor;
    private KeyboardLayout? _layout;
    private string? _previousWord;
    private string _recentText = ""; // Track recent text for smart capitalization

    public event Action<string>? KeyDwellCompleted;

    public KeyboardViewModel(EyeCtrlSettings settings, IMessenger messenger)
    {
        _settings = settings;
        _messenger = messenger;
        messenger.RegisterAll(this);
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
                NearestKeyProgress = 0;
                NearestKeyId = null;
            }
            return;
        }

        // Post-activation cooldown
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
            double dx = gazePoint.X - center.X;
            double dy = gazePoint.Y - center.Y;
            double dist = dx * dx + dy * dy;
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = id;
            }
        }

        if (nearestId != NearestKeyId)
        {
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
                NearestKeyProgress = 0;
                NearestKeyId = null;
                _keyCooldownEndTimestamp = timestampUs + (_cooldownMs * MathConstants.MicrosecondsPerMillisecond);
                KeyDwellCompleted?.Invoke(nearestId);
            }
        }
    }

    public void SetLayout(KeyboardLayout layout)
    {
        _layout = layout;
        ActiveSubLayoutId = layout.DefaultSubLayout;
    }

    public void SetPredictor(WordPredictor? predictor) => _predictor = predictor;

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
            _predictor?.LearnWord(actualOutput);
        }
        _messenger.Send(new KeyActivatedMessage(actualOutput, ModifiersSnapshot));
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
        _messenger.Send(new KeyActivatedMessage("\b", ModifiersSnapshot));
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
        _messenger.Send(new KeyActivatedMessage("\n", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleSpace()
    {
        if (!HasSystemModifiers)
        {
            if (CurrentInput.Length > 0)
            {
                _predictor?.LearnWord(CurrentInput);
                _previousWord = CurrentInput;
                AppendRecentText(CurrentInput);
            }
            CurrentInput = "";
        }
        _messenger.Send(new KeyActivatedMessage(" ", ModifiersSnapshot));
        ClearOneShotModifiers();
    }

    public void HandleTab()
    {
        _messenger.Send(new KeyActivatedMessage("\t", ModifiersSnapshot));
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
        if (_predictor == null) return new List<PredictionResult>();

        // Pass full text context for smart capitalization if enabled
        var fullText = _settings.Keyboard.SmartCapitalizationEnabled ? _recentText : null;

        return _predictor.GetSuggestions(CurrentInput, _previousWord, maxCount: 5, fullText: fullText);
    }

    public void HandlePredictionSelected(string word)
    {
        _predictor?.LearnWord(word);
        var deleteCount = CurrentInput.Length;
        CurrentInput = "";
        _previousWord = word;
        AppendRecentText(word);
        _messenger.Send(new PredictionSelectedMessage(word, deleteCount));
    }

    public void HandleSpecialKey(string keyName)
    {
        _messenger.Send(new SpecialKeyMessage(keyName, ModifiersSnapshot));
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

    public void Dispose()
    {
        if (_disposed) return;
        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
