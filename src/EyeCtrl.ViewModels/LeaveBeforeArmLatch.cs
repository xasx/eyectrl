namespace EyeCtrl.ViewModels;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Holds dwell targets disarmed until the gaze has been OUTSIDE each one's hit area at least once.
///
/// <para>
/// A <see cref="DwellDetector"/> starts a dwell on the first sample inside a target, including one
/// registered while the gaze already rests there. For the Settings window that means a resting gaze
/// answers the next modal on its own (the A2 review's cascade: one gaze at a fixed point answered
/// "Yes", then the next card, then the next question), and a resting gaze left over from a modal's
/// last button dwells whatever control the window shows under it once the modal closes.
/// </para>
///
/// <para>
/// The same idea as the window's <c>SettingsPagerRearmGuard</c> (re-arm once the gaze has left the
/// target's own circle, no timeout: a frozen stream accrues no dwell anywhere), but without
/// unregistering: a held target stays in the detector and is only filtered out of
/// <see cref="DwellDetector.Update(PhysicalPoint, long, Func{DwellTarget, bool})"/>, so its centre
/// keeps following the view's per-frame position push. The "outside" test is the detector's own
/// <see cref="DwellDetector.HitTest"/>, and it is trusted only once the target has had a real centre
/// pushed: a target registered at the placeholder origin would otherwise read "outside" and arm
/// before the view moves it under the gaze.
/// </para>
/// </summary>
internal sealed class LeaveBeforeArmLatch
{
    private readonly DwellDetector _detector;
    private readonly HashSet<string> _held = new();
    private readonly HashSet<string> _positioned = new();
    private readonly Func<DwellTarget, bool> _armedOnly;
    private readonly List<string> _due = new();

    internal LeaveBeforeArmLatch(DwellDetector detector)
    {
        _detector = detector;
        _armedOnly = t => !_held.Contains(t.Id);
    }

    /// <summary>The filter for the detector's update: null (no filtering) while nothing is held.</summary>
    internal Func<DwellTarget, bool>? Filter => _held.Count == 0 ? null : _armedOnly;

    /// <summary>Whether <paramref name="id"/> is held disarmed.</summary>
    internal bool IsHeld(string id) => _held.Contains(id);

    /// <summary>Holds <paramref name="id"/> until its centre is known and the gaze is outside it.</summary>
    internal void Hold(string id)
    {
        _held.Add(id);
        _positioned.Remove(id);
    }

    /// <summary>
    /// Holds every target the detector has now. Their centres are already real (the view pushed
    /// them while they were live), so they count as positioned.
    /// </summary>
    internal void HoldAll()
    {
        foreach (var t in _detector.Targets)
        {
            _held.Add(t.Id);
            _positioned.Add(t.Id);
        }
    }

    /// <summary>The view pushed a real centre for <paramref name="id"/>.</summary>
    internal void Positioned(string id)
    {
        if (_held.Contains(id))
            _positioned.Add(id);
    }

    /// <summary>Drops <paramref name="id"/> (unregistered).</summary>
    internal void Forget(string id)
    {
        _held.Remove(id);
        _positioned.Remove(id);
    }

    /// <summary>Drops everything (the category was cleared).</summary>
    internal void Clear()
    {
        _held.Clear();
        _positioned.Clear();
    }

    /// <summary>
    /// One gaze sample, BEFORE the detector's update: arms every held target that has a real centre
    /// and does not contain <paramref name="gaze"/>. Allocation-free.
    /// </summary>
    internal void Update(PhysicalPoint gaze)
    {
        if (_held.Count == 0) return;

        _due.Clear();
        foreach (var id in _held)
        {
            if (_positioned.Contains(id) && !_detector.HitTest(id, gaze))
                _due.Add(id);
        }

        foreach (var id in _due)
            Forget(id);
    }
}
