namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Accepting a prediction is a typing act, so it joins the key-activation click family (D1):
/// <see cref="KeyboardWindow.OnKeyDwellCompleted"/> clicks on every key activation, and the
/// prediction bar — the user's other word-producing surface — must do the same. The click sits
/// AFTER <c>HandlePredictionSelected</c> + <c>UpdatePredictions</c> and INSIDE the index guards
/// (S1): a sound failure (or a change to the call order) must never leave the bar stale, and the
/// no-op path for a stale index must stay silent.
///
/// <para>
/// The window is built with the off-screen <c>WS_POPUP</c> HwndSource probe from
/// <see cref="KeyboardPredictionExclusionTests"/> (its <c>Open</c> helper, copied verbatim): no
/// <c>Show()</c>, no visible window, CI-subset safe. The sound is observed through a recording
/// subclass of <see cref="KeyboardSoundService"/> itself — the method is virtual precisely so no
/// interface seam is needed here — whose base constructor points at a nonexistent wav, so no
/// audio device is ever touched.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardPredictionClickTests
{
    private const int WsPopup = unchecked((int)0x80000000);

    private const int KeyDwellMs = 450;
    private const int CooldownMs = 400;
    private const int PredictionDwellMs = 500;

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content,
        HwndSource Source, WeakReferenceMessenger Messenger, DwellDetector PredictionDetector)
        : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static KeyboardLayout ShippedQwerty() =>
        KeyboardLayoutLoader.Load(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Resources", "Layouts", "qwerty.json"));

    private static DwellDetector PredictionDetector(KeyboardWindow w) =>
        (DwellDetector)typeof(KeyboardWindow)
            .GetField("_predictionDwellDetector", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    /// <summary>
    /// A laid-out keyboard at effective scale 1.0 with a working prediction orchestrator, hosted
    /// in an HwndSource that is never made visible.
    /// </summary>
    private static Probe Open(Dictionary<string, int> words, ILanguageModelPredictor? lm = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;

        var vm = new KeyboardViewModel(settings, messenger);
        vm.SetOrchestrator(new PredictionOrchestrator(
            new WordPredictor(new InMemoryDictProvider(words), "QWERTY"), lmPredictor: lm));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(ShippedQwerty(), 1.0,
            keyDwellMs: KeyDwellMs, predictionDwellMs: PredictionDwellMs, cooldownMs: CooldownMs);

        var content = (FrameworkElement)w.Content;
        w.Content = null;

        var source = new HwndSource(new HwndSourceParameters("kb-prediction-click-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: UpdateGaze's gate would catch PointToScreen's throw and "
                + "return false, and every exclusion assertion would pass VACUOUSLY. Fix the "
                + "environment or the fixture, never delete this guard.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        return new Probe(w, vm, content, source, messenger, PredictionDetector(w));
    }

    private sealed class RecordingSoundService : KeyboardSoundService
    {
        public int Clicks;
        // Base ctor points at a nonexistent wav → null player → the override is the only sound path.
        public RecordingSoundService() : base(new KeyboardSettings(), "no-such-file.wav") { }
        public override void PlayKeyClick() => Clicks++;
    }

    [StaFact]
    public void PredictionAccept_PlaysTheKeyClick_AfterTheGuards()
    {
        var probe = Open(new Dictionary<string, int> { ["hello"] = 1000 });
        try
        {
            string? inserted = null;
            probe.Messenger.Register<PredictionSelectedMessage>(probe, (_, m) => inserted = m.Word);

            var recorder = new RecordingSoundService();
            probe.Window.SetSoundService(recorder);

            probe.Vm.CurrentInput = "hel";
            probe.Window.UpdatePredictions();
            probe.Content.UpdateLayout();
            probe.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), 0);
            Check.WithCustomMessage("The bar rendered nothing — nothing to accept.")
                .That(probe.Window.PredictionBar.Children.Count).IsStrictlyGreaterThan(0);

            probe.Window.OnPredictionDwelled("pred_0");

            Check.WithCustomMessage(
                    "Accepting the rendered suggestion did not play the key click — the "
                    + "prediction bar is a typing surface and belongs to the key-click family (D1).")
                .That(recorder.Clicks).IsEqualTo(1);
            Check.WithCustomMessage(
                    "pred_0 was not a real accept (no word inserted) — the click above may have "
                    + "ridden a no-op path. Fix the arrange, never the assertion.")
                .That(inserted).IsEqualTo("hello");

            probe.Window.OnPredictionDwelled("pred_9"); // stale index → no-op must NOT click
            Check.WithCustomMessage(
                    "A stale prediction index clicked: the no-op guard must stay silent, and the "
                    + "click must sit inside the rendered-index guards.")
                .That(recorder.Clicks).IsEqualTo(1);
        }
        finally { probe.Dispose(); }
    }
}

/// <summary>
/// The containment half of the key-click work (S1): <see cref="KeyboardSoundService.PlayKeyClick"/>
/// swallows and latches a <see cref="System.Media.SoundPlayer"/> failure, so a corrupt or
/// deleted-after-startup click.wav costs one log line instead of throwing on every key effect.
/// <c>SoundPlayer</c> validates the wav only in <c>Play()</c> (<c>Load()</c> accepts garbage —
/// measured in <see cref="DwellCueServiceTests.GarbageWav_NeverConstructsAPlayer"/>), and its
/// <c>Play</c> cannot be intercepted (not virtual — the reason the cue service got an
/// <c>ICuePlayer</c> seam, B1). This class therefore drives the latch through the REAL
/// <c>SoundPlayer</c> with a garbage wav, which <c>Play()</c> rejects deterministically
/// (measured: <c>InvalidOperationException</c>).
///
/// <para>
/// No window is involved — plain facts, outside the WpfApplication collection.
/// </para>
/// </summary>
public class KeyboardSoundServiceLatchTests
{
    [Fact]
    public void CorruptWav_FirstClickLatches_EveryLaterClickIsASilentNoOp()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"keyclick_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(bad, new byte[200]); // exists → player constructed; Play() rejects it
        try
        {
            var svc = new KeyboardSoundService(new KeyboardSettings(), bad); // click sound on by default

            Check.ThatCode(svc.PlayKeyClick).DoesNotThrow(); // one contained failure, one log line
            Check.ThatCode(svc.PlayKeyClick).DoesNotThrow(); // latched: never re-attempts Play()

            var player = typeof(KeyboardSoundService)
                .GetField("_player", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(svc);
            Check.WithCustomMessage(
                    "The service survived two clicks but did not latch: _player is still "
                    + "constructed, so every later key effect would re-attempt Play() and re-throw.")
                .That(player).IsNull();
        }
        finally { File.Delete(bad); }
    }

    [Fact]
    public void MissingWav_ConstructsNoPlayer_AndClicksAreSilentNoOps()
    {
        // The contract the recording double in KeyboardPredictionClickTests relies on: a
        // nonexistent wav must yield a null player, not a player that throws on first Play().
        var svc = new KeyboardSoundService(new KeyboardSettings(), $"no-such-{Guid.NewGuid():N}.wav");

        Check.ThatCode(svc.PlayKeyClick).DoesNotThrow();
        Check.ThatCode(svc.PlayKeyClick).DoesNotThrow();

        var player = typeof(KeyboardSoundService)
            .GetField("_player", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(svc);
        Check.That(player).IsNull();
    }
}
