namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Animation;
using EyeCtrl.Controls;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The activation feedback (B2/F1): a solid round button centred on the offered button's centre B,
/// drawn where the dwell is counted.
///
/// <para>
/// The face is a static 100 DIP disc with the toolbar's look and the one sign the action menu
/// gives it — a little ring menu, six open circles around a dot (<see cref="ActivationButtonFace"/>).
/// The sign is the same in every selection mode; there is no word. The dwell fills the disc from
/// the bottom in the accent colour; full = it fires. There is no growth, no motion and no arc head.
/// </para>
///
/// <para>
/// The window only positions itself and forwards progress/paused to the face. It is sized around
/// the disc plus a small edge margin, with no clamp: B is already clamped by the placement
/// function, and clamping again here would move the drawn centre off the counted point.
/// </para>
/// </summary>
public partial class ActivationButtonWindow : OverlayWindow, IActivationButtonOverlay
{
    // UI Layout Constants
    private const int FadeInDurationMs = 150; // Duration of fade-in animation
    private const int FadeOutDurationMs = 100; // Duration of fade-out animation
    private const double EdgeMarginDip = 2.0; // Slack between the disc's edge and the window edge

    private readonly ActivationButtonFace _face;

    public ActivationButtonWindow()
    {
        // Click-through: the feedback sits exactly where the user is looking and must not become a
        // mouse target over whatever is underneath it — the same contract the cursor ring has.
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // keep out of the magnifier/preview capture
        InitializeComponent();
        _face = new ActivationButtonFace();
        Content = _face;
        Opacity = 0;
        // The word is gone (F1), so the sign alone must carry a name for UIA: the same short
        // "Action menu" the sign depicts, never a mode name.
        AutomationProperties.SetName(this, StringTable.Current.T("overlays.activationButton.name"));
    }

    /// <summary>
    /// Centres the disc on <paramref name="buttonCenter"/> — the offer's B, in physical pixels.
    ///
    /// <para>
    /// <paramref name="hitRadiusPixels"/> is the offer's counted hit radius, carried by the show
    /// message for the offer contract. The drawn disc is deliberately smaller than it (100 DIP
    /// across versus a 150 px hit radius): the dwell is still counted exactly where the engine
    /// counts it, and "drawn = counted" means the drawn centre is B, not that the drawn size equals
    /// the hit size.
    /// </para>
    /// </summary>
    public void ShowAt(PhysicalPoint buttonCenter, double hitRadiusPixels)
    {
        double side = ActivationButtonFace.Diameter + (2 * EdgeMarginDip);
        Width = side;
        Height = side;

        Left = DpiHelper.PhysicalToLogicalX(buttonCenter.X) - (Width / 2);
        Top = DpiHelper.PhysicalToLogicalY(buttonCenter.Y) - (Height / 2);

        UpdateDwellProgress(0);
        SetFillPaused(false);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInDurationMs));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public new void Hide()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutDurationMs));
        fadeOut.Completed += (_, _) =>
        {
            base.Hide();
            // Torn down for the whole show half of the cycle: drop the progress too, so nothing stale
            // can be presented if the next show is late or its first frame is not pumped.
            UpdateDwellProgress(0);
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    public void HideImmediate()
    {
        // The fade-in is a HoldEnd animation, and a held animation outranks the base Opacity written
        // here — without clearing it first, the window would stay fully opaque while "hidden" and a
        // re-show could present the previous fill (its last height included) for a frame at the new
        // gaze point. Clearing it makes Opacity = 0 take effect, and the base value is what a
        // pump-less read observes.
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        UpdateDwellProgress(0);
        SetFillPaused(false);
        base.Hide();
    }

    /// <summary>Moves the fill's top edge and the content copies' clips for <paramref name="progress"/>.</summary>
    public void UpdateDwellProgress(double progress) => _face.UpdateProgress(progress);

    /// <summary>Dims the held fill while the engine's pause grace runs.</summary>
    /// <param name="paused">True while the dwell is paused.</param>
    public void SetFillPaused(bool paused) => _face.SetPaused(paused);

    /// <summary>The face, for the headless layout gates.</summary>
    internal ActivationButtonFace Face => _face;
}
