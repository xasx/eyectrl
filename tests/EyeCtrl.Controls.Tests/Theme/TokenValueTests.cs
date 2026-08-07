namespace EyeCtrl.Controls.Tests.Theme;

using System.Linq;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Guards the shape of the <see cref="Tokens"/> facade.
///
/// Token *values* are no longer pinned here: they live in tools/palette_validate.py
/// (which runs the ten gates against them) and are checked against the C# themes by
/// ThemeParityTests. Restating them here as well would have been a third copy.
/// </summary>
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
        Check.That(Tokens.Current.Name).IsEqualTo("Midnight");
    }

    [Fact]
    public void BorderThicknessStandard_IsOnePixelUnderMidnight()
    {
        Check.That(Tokens.BorderThicknessStandard).IsEqualTo(1.0);
    }
}
