namespace EyeCtrl.Sdk.WinUI;

using EyeCtrl.Contracts;

/// <summary>Pure coordinate composition for WinUI element → physical screen rect.</summary>
public static class WinUiCoords
{
    /// <summary>Composes an element's DIP position/size and the window client origin into a physical screen rect.</summary>
    /// <param name="dipX">Element X relative to the window content root, in DIPs (from TransformToVisual).</param>
    /// <param name="dipY">Element Y relative to the window content root, in DIPs.</param>
    /// <param name="dipW">Element ActualWidth in DIPs.</param>
    /// <param name="dipH">Element ActualHeight in DIPs.</param>
    /// <param name="scale">XamlRoot.RasterizationScale (DIP→physical).</param>
    /// <param name="clientOriginX">Client-area top-left X in physical screen px (from ClientToScreen(hwnd,{0,0})).</param>
    /// <param name="clientOriginY">Client-area top-left Y in physical screen px.</param>
    /// <remarks>Assumes the DIP position and the client origin refer to the same window — i.e. the
    /// XamlRoot content root sits at that window's client origin (true for a standard top-level WinUI Window).</remarks>
    public static SdkRect ToScreenRect(
        double dipX, double dipY, double dipW, double dipH,
        double scale, double clientOriginX, double clientOriginY)
        => new(
            clientOriginX + dipX * scale,
            clientOriginY + dipY * scale,
            dipW * scale,
            dipH * scale);
}
