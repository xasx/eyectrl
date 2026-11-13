namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media.Animation;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using Serilog;

public partial class SplashWindow : OverlayWindow
{
    private bool _sentenceShown;

    public SplashWindow()
    {
        InitializeComponent();

        // The splash thread has no Application resources: theme-derived values come from Tokens'
        // frozen brushes (they freeze at source — ThemeBase.CachedBrush), pinned colors are XAML
        // literals (spec §8). Fonts/radius already ride x:Static, which reads Tokens.Current at
        // parse time — the theme is activated before this thread starts.
        CardBorder.Background = Tokens.BrushSurfaceDark;
        TitleText.Foreground = Tokens.BrushTextPrimary;
        NoTrackerSentence.Foreground = Tokens.BrushTextPrimary;

        IsClickThrough = true;                          // L13: the card must never swallow dwells
    }

    /// <summary>The order repair would run from the splash thread and touch main-thread windows
    /// (r2 #4): the splash never asks for it.</summary>
    protected override bool NeedsOrderRepairAfterBandFallback => false;

    /// <summary>Test/verification accessor for the band-fallback override: true when this window
    /// opted OUT of the post-fallback order repair (NeedsOrderRepairAfterBandFallback is false).</summary>
    internal bool DeclaresNoOrderRepair => !NeedsOrderRepairAfterBandFallback;

    /// <summary>Whether ShowNoTrackerSentence has been called.</summary>
    internal bool SentenceShown => _sentenceShown;

    /// <summary>
    /// Whether the splash says "no eye tracker found" (Wave B B5): only when the provider FELL BACK
    /// to the mouse — a tracker was asked for (Auto, or a Tobii type) and the mouse is what resolved.
    /// Never when the mouse was chosen explicitly, and never in test mode, whose provider is always
    /// the mouse.
    /// </summary>
    /// <param name="requested">The provider type the settings asked for.</param>
    /// <param name="resolved">The provider type actually in use (decorators unwrapped).</param>
    /// <param name="isTestMode">Whether the app runs in test mode.</param>
    /// <returns>True when the sentence should show.</returns>
    internal static bool ShowsNoTrackerSentence(GazeProviderType requested, GazeProviderType resolved, bool isTestMode) =>
        !isTestMode && requested != GazeProviderType.Mouse && resolved == GazeProviderType.Mouse;

    /// <summary>
    /// Fades the no-tracker sentence in inside its always-reserved row (spec §7). The fade runs
    /// only when the window is visible (production); on an unshown window — tests, headless — the
    /// opacity is set directly so the outcome is deterministic.
    /// </summary>
    internal void ShowNoTrackerSentence()
    {
        if (_sentenceShown) return;
        _sentenceShown = true;
        try
        {
            if (IsVisible)
            {
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
                NoTrackerSentence.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            else
            {
                NoTrackerSentence.Opacity = 1;
            }
        }
        catch (Exception ex)
        {
            NoTrackerSentence.Opacity = 1;              // containment: shown statically beats lost
            Log.Warning(ex, "Splash: sentence fade failed; shown statically");
        }
    }

    /// <summary>Stub for Task 6's proxy — replaced by the beat glue in Task 5.</summary>
    internal void BeginExit() => ForceClose();

    /// <summary>Exception-contained hard close used by the fatal path and the dead-man timers.</summary>
    internal void ForceClose()
    {
        try { Close(); }
        catch (Exception ex) { Log.Warning(ex, "Splash: hard close failed"); }
    }
}
