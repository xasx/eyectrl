namespace EyeCtrl.Controls.Localization;

using System;
using System.Windows.Markup;
using EyeCtrl.Core.Localization;

/// <summary>
/// Minimal XAML markup extension for the process-global string table:
/// <c>Text="{loc:T 'settings.example'}"</c>. It lives in <c>EyeCtrl.Controls</c> because Core has
/// no WPF and the Supervisor references Core, Platform and Controls only.
/// </summary>
/// <remarks>
/// Resolution goes through <see cref="StringTable.Current"/>, which defaults to embedded English;
/// the value is read once when the XAML is loaded, matching the restart-to-apply language model.
/// </remarks>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    /// <summary>Creates the extension without a key; XAML assigns <see cref="Key"/>.</summary>
    public TExtension()
    {
    }

    /// <summary>Creates the extension for a key, for the positional <c>{loc:T 'key'}</c> form.</summary>
    /// <param name="key">The literal key to resolve.</param>
    public TExtension(string key)
    {
        Key = key;
    }

    /// <summary>The literal key to resolve, e.g. <c>settings.example</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        StringTable.Current.T(Key);
}
