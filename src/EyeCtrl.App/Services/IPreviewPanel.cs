namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

/// <summary>
/// The adaptation preview panel surface <see cref="AdaptationPreviewService"/> drives — the
/// <c>ICuePresenter</c> pattern: the real WPF window at runtime, a fake in the headless service
/// tests. Everything on the seam speaks physical pixels; the window converts at its live DPI.
/// </summary>
public interface IPreviewPanel
{
    /// <summary>Shows (or refreshes) the panel with fresh content at a fresh position.</summary>
    /// <param name="snapshot">The content (spec §3.4: four rows, fills, values, footer).</param>
    /// <param name="positionPhysical">The panel's top-left in physical px — the service's §3.3
    /// placement (beyond Reset's outer end, into the screen, work-area-clamped). The window divides
    /// by its live DPI scale.</param>
    /// <param name="dockedEdge">The strip's docked edge (<c>Top</c>/<c>Bottom</c>/<c>Left</c>/<c>Right</c>) —
    /// orients the connector tick toward Reset's end of the strip.</param>
    void ShowPanel(AdaptationPreviewSnapshot snapshot, PhysicalPoint positionPhysical, string dockedEdge);

    /// <summary>Hides the panel. Idempotent.</summary>
    void HidePanel();

    /// <summary>Raised with the live panel bounds in physical px after a show and on every size
    /// change, and with null when the panel hides. The KIND of absence is never the window's call —
    /// the service maps bounds to <see cref="EyeCtrl.ViewModels.Messages.PreviewBoundsKind"/> per P2.</summary>
    event Action<PhysicalRect?>? BoundsChanged;
}
