namespace EyeCtrl.App.Tests.Services;

using System.ComponentModel;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The A2 review's H1 probe, kept as a test: the real chain on the real
/// <see cref="SettingsWindowViewModel"/> modal path, its choices registered and positioned the way
/// <c>UpdateModalVisibility</c> and the per-frame push do — two choices at ±97 DIP around the card
/// centre, one choice at the centre, r70 hit circles (the window's ModalBtnRadius, at DPI 1) — and a
/// gaze that answers one card and then RESTS. Before the leave-before-arm latch, a gaze resting at
/// (−45, 0) answered the next card by itself (the circles overlap: 97 &lt; 70 + 70).
/// </summary>
public class SafetySetupGazeProbeTests
{
    private const double Radius = 70;
    private const double Side = 97;
    private const long FrameUs = 16_000;
    private const int RestMs = 6_700;

    private static readonly PhysicalPoint Centre = new(1000, 500);
    private static readonly PhysicalPoint Away = new(1000, 900);

    private readonly EyeCtrlSettings _settings = new();
    private readonly SettingsWindowViewModel _vm = QaTestSupport.MakeViewModel();
    private int _dwellMs;
    private long _now = 1_000_000;

    public SafetySetupGazeProbeTests()
    {
        _dwellMs = _settings.Dwell.ToolbarButtonDwellMs;
        _vm.PropertyChanged += RegisterLikeTheView;
    }

    [Theory]
    [InlineData(-45)]   // inside both the centre circle and the left answer's circle
    [InlineData(-97)]   // the left answer's centre
    public void AGazeThatAnswersQuestion1AndRests_NeverAnswersQuestion2(double x)
    {
        var flow = StartChain(supervision: null);
        Answer(x);                                       // Q1 "Yes" — deliberately
        Check.That(_vm.ModalMessage).StartsWith("Question 2 of 5");

        Gaze(At(x), RestMs);                             // … and the gaze rests

        Check.That(_vm.ModalMessage).StartsWith("Question 2 of 5");
        Check.That(_settings.General.StartOnBoot).IsFalse();
        GC.KeepAlive(flow);
    }

    [Fact]
    public void TheFailureCardsOk_AtTheSameSpot_DoesNotPassQuestion2()
    {
        // The failure card's single OK sits at the centre; the next question's "Yes" at −97. A gaze
        // at (−45, 0) is inside both.
        var unavailable = new SupervisionToggleService(
            _settings,
            new SupervisorBringUp(_settings, () => { }, () => false, _ => { }, () => { }, () => false),
            new SupervisorStartupEnsure(_settings, () => { }, () => false, () => false),
            persistEnabled: _ => { }, supervisorInstalled: () => false, isSupervisorConnected: () => false,
            reconcileRunKey: () => { }, runInBackground: w => w(), postToUi: w => w());
        var flow = StartChain(unavailable);
        Answer(-Side);                                   // Q1 "Yes" → the failure card
        Check.That(_vm.ModalChoices.Select(c => c.Id)).ContainsExactly("safety_ok");

        Answer(-45);                                     // OK, deliberately, at (−45, 0)
        Check.That(_vm.ModalMessage).StartsWith("Question 2 of 5");
        Gaze(At(-45), RestMs);

        Check.That(_vm.ModalMessage).StartsWith("Question 2 of 5");
        Check.That(_settings.General.StartOnBoot).IsFalse();
        GC.KeepAlive(flow);
    }

    [Fact]
    public void TheLastAnswer_FromTheRow_DoesNotDwellTheRowUnderTheRestingGaze()
    {
        // Started from the General row: the window stays open. A row sits exactly where "Basic" was.
        _vm.Register("g_boot", DwellCategory.Control, Radius, _dwellMs, () => _settings.General.StartPaused = true);
        _vm.UpdatePosition("g_boot", DwellCategory.Control, At(-Side));
        var flow = StartChain(supervision: null);
        for (var i = 0; i < 4; i++) Answer(+Side);       // "No" × 4
        Answer(-Side);                                   // "Basic" — the last answer
        Check.That(_vm.IsModalVisible).IsFalse();

        Gaze(At(-Side), RestMs);

        Check.That(_settings.General.StartPaused).IsFalse();
        GC.KeepAlive(flow);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private SafetySetupFlow StartChain(SupervisionToggleService? supervision)
    {
        var flow = new SafetySetupFlow(_settings, supervision,
            (message, choices) => _vm.ShowModal(message, choices), () => { }, _ => { });
        flow.Start();
        Answer(-Side);                                   // "✓ Start"
        return flow;
    }

    /// <summary>A deliberate answer: the gaze comes from elsewhere and dwells at <paramref name="x"/>
    /// until the card changes.</summary>
    private void Answer(double x)
    {
        var before = (_vm.ModalMessage, _vm.ModalChoices);
        Gaze(Away, 100);
        for (var ms = 0; ms < 5_000 && (_vm.ModalMessage, _vm.ModalChoices) == before; ms += 16)
            Gaze(At(x), 16);
        Check.WithCustomMessage($"The card did not change after a deliberate dwell at x={x}.")
            .That((_vm.ModalMessage, _vm.ModalChoices) == before).IsFalse();
    }

    private static PhysicalPoint At(double x) => new(Centre.X + x, Centre.Y);

    private void Gaze(PhysicalPoint at, int ms)
    {
        for (long elapsed = 0; elapsed < ms * 1000L; elapsed += FrameUs)
        {
            _now += FrameUs;
            _vm.Receive(new GazeCursorMovedMessage(at, _now));
        }
    }

    /// <summary>What the window does for a card: one target per choice, two side by side at ±97,
    /// one in the middle, positioned at once (the per-frame push).</summary>
    private void RegisterLikeTheView(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsWindowViewModel.IsModalVisible) || !_vm.IsModalVisible) return;

        var choices = _vm.ModalChoices;
        for (var i = 0; i < choices.Count; i++)
        {
            var x = choices.Count == 1 ? 0 : (i == 0 ? -Side : +Side);
            _vm.Register(choices[i].Id, DwellCategory.Modal, Radius, _dwellMs, () => { });
            _vm.UpdatePosition(choices[i].Id, DwellCategory.Modal, At(x));
        }
    }
}
