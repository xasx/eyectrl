namespace EyeCtrl.Controls.Tests;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The button silhouette must read against any desktop content behind it.
///
/// <c>[StaFact]</c> because Create builds WPF shapes. No window is created.
/// </summary>
public class DwellArcButtonRimTests
{
    private const double Radius = 70;

    /// <summary>
    /// How far past the button radius the halo may reach. The action-menu window's only slack is
    /// half of <c>RingPlan.WindowSizeMargin</c> (10 DIP), so anything under that is safe;
    /// this pins the number so a later widening cannot silently clip the outermost buttons.
    /// </summary>
    private const double ActionMenuRimOvershoot = 4;

    [StaFact]
    public void TheRim_IsTwoDipOfBorderSubtle()
    {
        var button = DwellArcButton.Create(new Point(0, 0), Radius, symbol: null);

        Check.That(button.Background.StrokeThickness).IsEqualTo(2.0);
        Check.That(button.Background.Stroke).IsSameReferenceAs(Tokens.BrushBorderSubtle);
    }

    [StaFact]
    public void TheRimHalo_SitsOutsideTheButton_AndDoesNotOverlapTheRim()
    {
        var button = DwellArcButton.Create(new Point(0, 0), Radius, symbol: null);
        var halo = button.RimHalo;

        Check.WithCustomMessage("No rim halo was built.").That(halo).IsNotNull();

        // Stroke straddles the ellipse edge, so the painted band is centre-radius +/- half.
        var haloCentreRadius = halo!.Width / 2;
        var inner = haloCentreRadius - halo.StrokeThickness / 2;
        var outer = haloCentreRadius + halo.StrokeThickness / 2;

        // The rim's outer edge is radius + 1 (2 DIP straddling radius).
        Check.WithCustomMessage($"The halo starts at {inner}, inside the rim's outer edge {Radius + 1}.")
             .That(inner).IsGreaterOrEqualThan(Radius + 1);
        Check.WithCustomMessage($"The halo reaches {outer}, past the {ActionMenuRimOvershoot} DIP budget.")
             .That(outer).IsLessOrEqualThan(Radius + ActionMenuRimOvershoot);
        Check.That(halo.Stroke).IsSameReferenceAs(Tokens.BrushStrokeHalo);
        Check.That(halo.Fill).IsNull();
    }

    [StaFact]
    public void TheRimHalo_IsPaintedBehindTheFill()
    {
        // Order matters: the halo is a full ring, and painting it after the background would lay
        // dark over the light rim's outer half, collapsing the light/dark edge pair this exists for.
        var canvas = new Canvas();
        DwellArcButton.Create(new Point(0, 0), Radius, symbol: null).AddTo(canvas);

        var halo = canvas.Children.OfType<Ellipse>().First(e => e.Fill is null);
        var fill = canvas.Children.OfType<Ellipse>().First(e => e.Fill is not null);

        Check.That(canvas.Children.IndexOf(halo)).IsStrictlyLessThan(canvas.Children.IndexOf(fill));
    }
}
