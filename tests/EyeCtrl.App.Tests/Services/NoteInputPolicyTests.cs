namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services.Notes;
using NFluent;
using Xunit;

/// <summary>
/// T6 P5: the caretaker-input accept/ignore policy, exhaustively. Touch acts on a drawn,
/// registered control whether or not EyeCtrl is paused; Mouse acts only while paused, never
/// when the event was promoted from touch, and never on a hidden or unregistered control.
/// </summary>
public class NoteInputPolicyTests
{
    private const int Rows = 32;

    private static (bool Kind, bool Paused, bool Stylus, bool Visible, bool Registered)[] AllRows()
    {
        var rows = new (bool, bool, bool, bool, bool)[Rows];
        int i = 0;
        foreach (var touch in new[] { false, true })
            foreach (var paused in new[] { false, true })
                foreach (var stylus in new[] { false, true })
                    foreach (var visible in new[] { false, true })
                        foreach (var registered in new[] { false, true })
                            rows[i++] = (touch, paused, stylus, visible, registered);
        return rows;
    }

    [Fact]
    public void TheDecisionTable_IsExhaustive_AndMatchTheRules()
    {
        var decided = new System.Collections.Generic.HashSet<(bool, bool, bool, bool, bool)>();
        foreach (var (touch, paused, stylus, visible, registered) in AllRows())
        {
            var kind = touch ? NoteInputKind.Touch : NoteInputKind.Mouse;
            bool accepts = NoteInputPolicy.Accepts(kind, paused, stylus, visible, registered);
            Check.That(decided.Add((touch, paused, stylus, visible, registered)))
                .IsTrue(); // no duplicate rows: the sweep is the full 2^5 table

            bool expected = touch
                ? visible && registered
                : paused && !stylus && visible && registered;
            Check.WithCustomMessage($"kind={(touch ? "touch" : "mouse")} paused={paused} "
                    + $"stylus={stylus} visible={visible} registered={registered}")
                .That(accepts).IsEqualTo(expected);
        }

        Check.WithCustomMessage("the sweep must cover every combination")
            .That(decided.Count).IsEqualTo(Rows);
    }

    [Fact]
    public void Touch_ActsWhetherPausedOrNot()
    {
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Touch, paused: false, fromStylus: false,
            controlVisible: true, controlRegistered: true)).IsTrue();
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Touch, paused: true, fromStylus: false,
            controlVisible: true, controlRegistered: true)).IsTrue();
    }

    [Fact]
    public void Touch_NeverActsOnAHiddenOrUnregisteredControl()
    {
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Touch, paused: false, fromStylus: false,
            controlVisible: false, controlRegistered: true)).IsFalse();
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Touch, paused: false, fromStylus: false,
            controlVisible: true, controlRegistered: false)).IsFalse();
    }

    [Fact]
    public void Mouse_ActsOnlyWhilePaused()
    {
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Mouse, paused: true, fromStylus: false,
            controlVisible: true, controlRegistered: true)).IsTrue();
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Mouse, paused: false, fromStylus: false,
            controlVisible: true, controlRegistered: true)).IsFalse();
    }

    [Fact]
    public void Mouse_IgnoresAStylusPromotionEvent_EvenWhilePaused()
    {
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Mouse, paused: true, fromStylus: true,
            controlVisible: true, controlRegistered: true)).IsFalse();
    }

    [Fact]
    public void Mouse_NeverActsOnAHiddenOrUnregisteredControl_EvenWhilePaused()
    {
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Mouse, paused: true, fromStylus: false,
            controlVisible: false, controlRegistered: true)).IsFalse();
        Check.That(NoteInputPolicy.Accepts(NoteInputKind.Mouse, paused: true, fromStylus: false,
            controlVisible: true, controlRegistered: false)).IsFalse();
    }
}
