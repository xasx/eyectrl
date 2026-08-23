namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Guards the shape of the <see cref="Tokens"/> facade.
///
/// Token *values* are no longer pinned here: colour values live in tools/palette_validate.py
/// (which runs the ten gates against them) and are checked against the C# themes by
/// ThemeParityTests. Restating them here as well would have been a third copy.
///
/// The geometry and type ramp values additionally carry a deliberate value freeze in
/// ThemeShapeTests.AllThreeThemes_DeclareIdenticalGeometryAndType, because the validator's
/// gates check only their ordering (plus a 12px floor), never the exact numbers.
/// </summary>
[Collection("ActiveTheme")]
public class TokenValueTests
{
    [Fact]
    public void EveryColourToken_HasAMatchingFrozenBrush()
    {
        foreach (var property in typeof(Tokens).GetProperties()
                     .Where(p => p.PropertyType == typeof(Color)))
        {
            var brushProperty = typeof(Tokens).GetProperty("Brush" + property.Name);
            Check.WithCustomMessage($"Tokens.Brush{property.Name} is missing")
                .That(brushProperty).IsNotNull();

            var brush = (SolidColorBrush)brushProperty!.GetValue(null)!;
            Check.That(brush.Color).IsEqualTo((Color)property.GetValue(null)!);
            Check.WithCustomMessage($"Tokens.Brush{property.Name} must be frozen")
                .That(brush.IsFrozen).IsTrue();
        }
    }

    [Fact]
    public void Tokens_ExposesEveryMemberOfITheme()
    {
        // The facade must not fall behind the interface: a token added to ITheme
        // and not forwarded here is invisible to all 131 C# call sites.
        foreach (var member in typeof(ITheme).GetProperties()
                     .Where(p => p.Name != nameof(ITheme.Name)))
        {
            Check.WithCustomMessage(
                    $"ITheme.{member.Name} has no forwarding property on Tokens. "
                    + "Add it, or the token is unreachable from C# and XAML.")
                .That(typeof(Tokens).GetProperty(member.Name)).IsNotNull();
        }
    }

    [Fact]
    public void EveryTokenForwarder_ReturnsTheCurrentThemesValue()
    {
        // Tokens_ExposesEveryMemberOfITheme proves a forwarder EXISTS; this proves it
        // forwards. Without it, `=> 13.0` or `=> Current.FontSizeBodyLarge` in place of
        // `=> Current.FontSizeBody` passes the entire suite.
        foreach (var member in typeof(ITheme).GetProperties()
                     .Where(p => p.Name != nameof(ITheme.Name)))
        {
            var forwarder = typeof(Tokens).GetProperty(member.Name);
            Check.WithCustomMessage(
                    $"Tokens.{member.Name} must forward Current, not restate a value.")
                .That(forwarder!.GetValue(null)).IsEqualTo(member.GetValue(Tokens.Current));
        }
    }

    [Fact]
    public void DefaultTheme_IsMidnight()
    {
        // Tests and any pre-startup code path must see the shipping palette.
        //
        // Deliberately reads the ambient theme: this is the canary for the
        // [Collection("ActiveTheme")] contract, so a Tokens.Activate caller that fails to
        // restore Midnight breaks HERE, loudly, instead of skewing some later reader that
        // happens to run after it. Do not "fix" this by activating Midnight first — that
        // would make it assert nothing.
        Check.That(Tokens.Current.Name).IsEqualTo("Midnight");
    }

    [Fact]
    public void BorderThicknessStandard_IsOnePixelUnderMidnight()
    {
        // Pins the theme rather than trusting the ambient one. Tokens.Current is
        // process-global mutable state, so reading a Midnight-SPECIFIC value through the
        // facade made this test's outcome depend on what ran before it — it passed only
        // because no Controls.Tests test calls Tokens.Activate yet. Unlike
        // DefaultTheme_IsMidnight, the ambient theme is not this test's subject: the
        // subject is that the facade reports 1px when Midnight is the active theme.
        var previous = Tokens.Current;
        try
        {
            Tokens.Activate(MidnightTheme.Instance);
            Check.That(Tokens.BorderThicknessStandard).IsEqualTo(1.0);
        }
        finally
        {
            Tokens.Activate(previous);
        }
    }
}
