namespace EyeCtrl.Controls;

using System;
using System.Globalization;
using System.Windows.Data;

/// <summary>
/// Binds a held-dwell flag to the progress indicator's opacity (<see cref="DwellPausedCue"/>), for
/// the XAML progress bars. A converter rather than a style trigger so the bar's own
/// <c>Opacity="{Binding ...}"</c> attribute is the only opacity writer — the window-opacity guard
/// in the test suite rejects any <c>Setter Property="Opacity"</c> in an overlay's XAML.
/// </summary>
public sealed class DwellPausedOpacityConverter : IValueConverter
{
    /// <summary>The shared instance, for <c>{x:Static}</c> in XAML.</summary>
    public static DwellPausedOpacityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DwellPausedCue.OpacityFor(value is true);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
