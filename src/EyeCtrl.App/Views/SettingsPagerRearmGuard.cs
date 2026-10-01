namespace EyeCtrl.App.Views;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Re-arm guard for a pager button that just wrapped (jumped) to the far end of the settings
/// content pages or sidebar tab pages.
///
/// <para>
/// Without it, a gaze that stays on the jumped button re-dwells it as soon as the detector's
/// cooldown elapses. At a boundary the button's job has just changed — the jump-to-end button is
/// now a step-back button — so the same resting gaze walks the list one page per dwell
/// (0 → last → last-1 → …), which is the oscillation this guard exists to stop.
/// </para>
///
/// <para>
/// The guard works by UNREGISTERING the jumped target: a target that is not registered cannot
/// accumulate dwell at all. It re-registers the id when the gaze has left the jumped button's
/// OWN hit circle — its circle alone, not both pagers' — and there is deliberately NO timeout
/// fallback. A frozen gaze stream sends no frames, and without frames nothing accrues dwell
/// anywhere, so a guard that never re-arms costs nothing. (An earlier fallback of 2× the jump
/// dwell was dropped: its clock also only advanced on frames, so it fired exactly in the
/// resting-gaze case the guard exists to suppress — re-arming under a motionless gaze and
/// reopening the walk at step rate.)
/// </para>
///
/// <para>
/// The hit circle is the target's own geometry: the centre is captured at disarm time from
/// LAID-OUT state only (the caller passes the position loop's last pushed centre — a freshly
/// re-rendered Border reads as the panel origin, 70–240 DIP off) and the radius is the same
/// physical radius the button registers with. The settings window is fixed-size, so the circle
/// captured at disarm time stays valid for the whole hold. View-owned: no ViewModel change, the
/// guard only calls the <see cref="IDwellRegistrar"/> the view already has.
/// </para>
/// </summary>
internal sealed class SettingsPagerRearmGuard
{
    private readonly IDwellRegistrar _registrar;
    private readonly Action<string, DwellCategory> _onRearm;

    private sealed class Guarded
    {
        public DwellCategory Category;
        public PhysicalPoint Center;
        public double RadiusPhysical;
    }

    private readonly Dictionary<string, Guarded> _guarded = new();

    /// <summary>Creates a guard over <paramref name="registrar"/>.</summary>
    /// <param name="registrar">The dwell registrar the view registers pager targets with.</param>
    /// <param name="onRearm">Re-registers one id for its CURRENT page state — the button's action
    /// and dwell may have changed while it was guarded, so the view recomputes them at re-arm.</param>
    internal SettingsPagerRearmGuard(IDwellRegistrar registrar, Action<string, DwellCategory> onRearm)
    {
        _registrar = registrar;
        _onRearm = onRearm;
    }

    /// <summary>True while <paramref name="id"/> is held disarmed. Registration sites consult
    /// this so a re-render cannot silently re-arm a jumped button.</summary>
    internal bool IsDisarmed(string id) => _guarded.ContainsKey(id);

    /// <summary>
    /// Disarms <paramref name="id"/>: unregisters its dwell target (resetting any accumulated
    /// state with it) and remembers the hit circle the gaze must leave.
    /// </summary>
    internal void Disarm(
        string id, DwellCategory category, PhysicalPoint center, double radiusPhysical)
    {
        _registrar.Unregister(id, category);
        _guarded[id] = new Guarded
        {
            Category = category,
            Center = center,
            RadiusPhysical = radiusPhysical,
        };
    }

    /// <summary>
    /// One gaze frame: re-arms every guarded button whose gaze has left its hit circle. Runs
    /// before the frame's position update, so a re-registered target gets its centre pushed in
    /// the same pass. Allocation-free while nothing is guarded.
    /// </summary>
    internal void Update(PhysicalPoint gaze)
    {
        if (_guarded.Count == 0) return;

        List<string>? due = null;
        foreach (var (id, g) in _guarded)
        {
            var dx = gaze.X - g.Center.X;
            var dy = gaze.Y - g.Center.Y;
            if (dx * dx + dy * dy > g.RadiusPhysical * g.RadiusPhysical)
                (due ??= []).Add(id);
        }

        if (due is null) return;
        foreach (var id in due)
        {
            if (!_guarded.Remove(id, out var g)) continue;
            _onRearm(id, g.Category);
        }
    }

    /// <summary>Drops the CONTENT pager's guard state without re-registering anything. Called
    /// when the content pager itself disappears (a tab repaginated to one page), where a re-arm
    /// would register a hit circle over chrome that is no longer drawn. Scoped to the pag_* ids
    /// on purpose: an unscoped clear would drop a held TAB guard WITHOUT re-registering it —
    /// and since the guard's mechanism IS unregistration, that tab button would be left
    /// unregistered (dead to dwell) until its next re-registration site — so the tab guards
    /// keep their own lifecycle.</summary>
    internal void ClearContentPager()
    {
        _guarded.Remove("pag_prev");
        _guarded.Remove("pag_next");
    }
}
