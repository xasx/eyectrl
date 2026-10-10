namespace EyeCtrl.App.Tests.TestSupport;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;

/// <summary>
/// Activates a theme for a test the way <c>ThemeStartup.Apply</c> does at startup —
/// <see cref="Tokens.Activate"/> plus the theme's published resources into the Application
/// dictionary, which overlay windows resolve their <c>StaticResource</c>s against — and restores
/// both on dispose: Midnight, and the dictionary's exact keys and values. Both are process-global,
/// so a test using this belongs in the exclusive "ActiveTheme" collection.
/// </summary>
internal sealed class ThemeScope : IDisposable
{
    private readonly ResourceDictionary _resources;
    private readonly Dictionary<object, object?> _before;

    private ThemeScope(ResourceDictionary resources)
    {
        _resources = resources;
        _before = resources.Keys.Cast<object>().ToDictionary(k => k, k => (object?)resources[k]);
    }

    /// <summary>Activates <paramref name="theme"/> until the scope is disposed.</summary>
    internal static ThemeScope Activate(ITheme theme)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var scope = new ThemeScope(Application.Current.Resources);
        Tokens.Activate(theme);
        ThemeResourceApplier.Apply(Application.Current.Resources, theme);
        return scope;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Tokens.Activate(MidnightTheme.Instance);
        foreach (var key in _resources.Keys.Cast<object>().ToList())
        {
            if (!_before.ContainsKey(key)) _resources.Remove(key);
        }

        foreach (var (key, value) in _before) _resources[key] = value;
    }
}
