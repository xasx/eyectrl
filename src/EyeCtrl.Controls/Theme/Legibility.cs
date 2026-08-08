namespace EyeCtrl.Controls.Theme;

/// <summary>
/// Absolute pixel floors that exist for legibility, not for style.
///
/// **Why this is not on <see cref="ITheme"/> and not on <see cref="ContainerRatio"/>.** A
/// <see cref="ContainerRatio"/> member is a fraction in (0, 1) — its tests reflect over every
/// public static and assert exactly that, so a pixel value there fails
/// <c>EveryRatio_JoinsTheOrderedChainAndIsDistinct</c>. And a theme member would be
/// theme-dependent, which is the wrong layering: a minimum size a user must be able to read is
/// not something a palette gets to renegotiate.
///
/// **Why it is not simply <c>Tokens.FontSizeBody</c>, which is also 12.** The four clamp sites
/// that read <see cref="MinimumGlyphFontSize"/> want a *minimum*, not the body step of the type
/// ramp. Those two happen to coincide today, and the coincidence hid a real coupling: the
/// validator (tools/palette_validate.py) and <c>ThemeShapeTests</c> gate
/// <c>FontSizeBody &gt;= 12</c>, not <c>== 12</c>, so raising body text to 14 — an entirely
/// reasonable in-ramp change that both guards permit — would silently grow four keyboard and
/// emoji glyph sizes at the bottom of the multiplier range. Naming the minimum separately makes
/// the ramp free to move without dragging the floor with it.
/// </summary>
public static class Legibility
{
    /// <summary>
    /// The smallest font size any glyph may render at, in DIPs.
    ///
    /// Used as the lower bound where a glyph is sized from a container that the user can shrink
    /// — <c>KeyboardWindow</c>'s keys scale with <c>KeySizeMultiplier</c>, and at the bottom of
    /// that range a ratio alone lands below what is readable.
    ///
    /// Equal to the floor tools/palette_validate.py gates <c>FontSizeBody</c> against (spec §8.4
    /// chose 12 as the accessibility floor; §9 gates it). That equality is asserted by
    /// <c>LegibilityFloorTests</c>, which parses the gate out of the validator rather than
    /// restating the number, so the two cannot drift apart. It is deliberately NOT read from
    /// <c>Tokens.FontSizeBody</c>: that is a ramp step and is free to rise above the floor.
    /// </summary>
    public const double MinimumGlyphFontSize = 12;
}
