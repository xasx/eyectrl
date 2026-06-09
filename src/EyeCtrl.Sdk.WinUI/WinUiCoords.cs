namespace EyeCtrl.Sdk.WinUI;

using EyeCtrl.Contracts;

/// <summary>Pure coordinate composition for WinUI element → physical screen rect.</summary>
public static class WinUiCoords
{
    /// <param name="dipX">Element X relative to the window content root, in DIPs (from TransformToVisual).</param>
    /// <param name="dipY">Element Y relative to the window content root, in DIPs.</param>
    /// <param name="dipW">Element ActualWidth in DIPs.</param>
    /// <param name="dipH">Element ActualHeight in DIPs.</param>
    /// <param name="scale">XamlRoot.RasterizationScale (DIP→physical).</param>
    /// <param name="clientOriginX">Client-area top-left X in physical screen px (from ClientToScreen(hwnd,{0,0})).</param>
    /// <param name="clientOriginY">Client-area top-left Y in physical screen px.</param>
    public static SdkRect ToScreenRect(
        double dipX, double dipY, double dipW, double dipH,
        double scale, double clientOriginX, double clientOriginY)
        => new(
            clientOriginX + dipX * scale,
            clientOriginY + dipY * scale,
            dipW * scale,
            dipH * scale);
}
