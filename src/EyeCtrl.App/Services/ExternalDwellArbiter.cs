namespace EyeCtrl.App.Services;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public readonly record struct ArbiterResult(
    string? FeedbackId, PhysicalRect? FeedbackRect, double Progress, string? ActivatedId, bool Suppress)
{
    public static readonly ArbiterResult None = new(null, null, 0, null, false);
}

public sealed class ExternalDwellArbiter
{
    private readonly Func<PhysicalPoint, long?> _rootWindowAtPoint;
    private readonly Dictionary<string, DwellTarget> _targets = new();
    private readonly Dictionary<string, long> _hwndById = new();
    private long _cooldownUs;
    private long? _lastActivationUs;

    public ExternalDwellArbiter(Func<PhysicalPoint, long?> rootWindowAtPoint, int cooldownMs)
    {
        _rootWindowAtPoint = rootWindowAtPoint;
        _cooldownUs = (long)cooldownMs * MathConstants.MicrosecondsPerMillisecond;
    }

    public void SetCooldown(int cooldownMs) => _cooldownUs = (long)cooldownMs * MathConstants.MicrosecondsPerMillisecond;

    public void AddOrUpdate(string id, PhysicalRect rect, long hwnd, int dwellMs)
    {
        if (_targets.TryGetValue(id, out var existing))
        {
            existing.Bounds = rect;
            existing.Center = rect.Center;
        }
        else
        {
            _targets[id] = new DwellTarget(id, rect, dwellMs);
        }
        _hwndById[id] = hwnd;
    }

    public void Remove(string id)
    {
        _targets.Remove(id);
        _hwndById.Remove(id);
    }

    public void Clear()
    {
        _targets.Clear();
        _hwndById.Clear();
        _lastActivationUs = null;
    }

    public ArbiterResult Process(PhysicalPoint gaze, long timestampUs)
    {
        if (_lastActivationUs is { } last && timestampUs - last < _cooldownUs)
        {
            ResetAll();
            return ArbiterResult.None;
        }
        _lastActivationUs = null;

        long? root = _targets.Count == 0 ? null : _rootWindowAtPoint(gaze);

        string? nearestId = null;
        double nearestDistSq = double.MaxValue;
        if (root is { } r)
        {
            foreach (var (id, t) in _targets)
            {
                if (!_hwndById.TryGetValue(id, out var h) || h != r) continue;
                if (!t.HitTest(gaze)) continue;
                double dx = gaze.X - t.Center.X, dy = gaze.Y - t.Center.Y;
                double d = dx * dx + dy * dy;
                if (d < nearestDistSq) { nearestDistSq = d; nearestId = id; }
            }
        }

        string? activeId = null, activatedId = null;
        double progress = 0;

        foreach (var (id, t) in _targets)
        {
            if (id == nearestId)
            {
                if (!t.IsGazeInside)
                {
                    t.IsGazeInside = true;
                    t.EnteredTimestamp = timestampUs;
                    activeId = id;
                }
                else if (t.EnteredTimestamp is { } entered)
                {
                    long elapsed = timestampUs - entered;
                    if (elapsed >= t.DwellTimeMicroseconds)
                    {
                        activatedId = id;
                        t.IsGazeInside = false;
                        t.EnteredTimestamp = null;
                        _lastActivationUs = timestampUs;
                    }
                    else
                    {
                        activeId = id;
                        progress = Math.Min(1.0, (double)elapsed / t.DwellTimeMicroseconds);
                    }
                }
            }
            else
            {
                t.IsGazeInside = false;
                t.EnteredTimestamp = null;
            }
        }

        if (activatedId != null)
            return new ArbiterResult(null, null, 0, activatedId, Suppress: false);
        if (activeId != null && _targets[activeId].Bounds is { } b)
            return new ArbiterResult(activeId, b, progress, null, Suppress: true);
        return ArbiterResult.None;
    }

    private void ResetAll()
    {
        foreach (var t in _targets.Values) { t.IsGazeInside = false; t.EnteredTimestamp = null; }
    }
}
