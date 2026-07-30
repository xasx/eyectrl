namespace EyeCtrl.Controls.Theme;

using System;
using System.Windows;
using System.Windows.Markup;

/// <summary>
/// Produces a uniform <see cref="Thickness"/> from a <see cref="double"/> token, so a
/// thickness token can live in <c>OverlayStyles.xaml</c> alongside the colours instead of
/// being injected imperatively at application startup.
///
/// WPF has no XAML syntax for building a <see cref="Thickness"/> from an <c>{x:Static}</c>
/// <see cref="double"/> — a <c>double</c> resource will not convert, and both the
/// inner-text and child-element forms fail at runtime. The first fix for that assembled the
/// <see cref="Thickness"/> in <c>App.OnStartup</c>, which worked but moved the token's XAML
/// surface out of the theme dictionary: <c>TokenDictionaryTests</c> could no longer see it,
/// the load test had to *replicate* the startup wiring rather than exercise it, and any
/// consumer outside <c>EyeCtrl.App</c> would have found the key missing.
///
/// A markup extension keeps it declarative and inside the dictionary, so the thickness is
/// drift-guarded by exactly the same test as every brush.
/// </summary>
[MarkupExtensionReturnType(typeof(Thickness))]
public sealed class UniformThicknessExtension : MarkupExtension
{
    /// <summary>Creates the extension with no value set; <see cref="Value"/> must be assigned.</summary>
    public UniformThicknessExtension()
    {
    }

    /// <summary>Creates the extension from a uniform thickness in device-independent pixels.</summary>
    public UniformThicknessExtension(double value) => Value = value;

    /// <summary>Uniform thickness applied to all four sides.</summary>
    public double Value { get; set; }

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Thickness(Value, Value, Value, Value);
}
