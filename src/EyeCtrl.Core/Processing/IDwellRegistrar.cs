namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public enum DwellCategory { Tab, Control, Pagination, Modal }

/// <summary>
/// Contract for dwell target registration, position updates, and progress queries.
/// Each overlay that uses dwell detection implements this independently,
/// wrapping its own DwellDetector instance(s).
/// </summary>
public interface IDwellRegistrar
{
    void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated);
    void Unregister(string id, DwellCategory category);
    void UpdatePosition(string id, DwellCategory category, PhysicalPoint center);
    double GetProgress(string id, DwellCategory category);
}
