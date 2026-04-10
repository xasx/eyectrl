namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

/// <summary>
/// Identifies which UI section a dwell target belongs to.
/// Each category is backed by an independent <see cref="DwellDetector"/>, so
/// targets in different categories never interfere with each other's dwell state.
/// </summary>
public enum DwellCategory
{
    /// <summary>Sidebar navigation tabs and the close button.</summary>
    Tab,

    /// <summary>Settings controls on the active page (sliders, toggles, buttons, etc.).</summary>
    Control,

    /// <summary>Previous / next pagination buttons.</summary>
    Pagination,

    /// <summary>
    /// Yes / No buttons inside a modal confirmation dialog.
    /// The built-in Yes/No logic (invoking the <c>onYes</c> callback and hiding the dialog)
    /// always runs first; the <c>onActivated</c> callback passed to
    /// <see cref="IDwellRegistrar.Register"/> is invoked afterwards as an extension point.
    /// </summary>
    Modal,
}

/// <summary>
/// Contract for registering, positioning, and querying gaze-dwell targets.
/// </summary>
/// <remarks>
/// Implemented by the ViewModel of each overlay window so that
/// <see cref="Controls.DwellControl"/> sub-classes can self-register without
/// taking a direct dependency on the WPF layer or a concrete ViewModel type.
/// All positions must be supplied in physical screen pixels (device pixels) to
/// match the coordinates produced by the Tobii eye-tracker.
/// </remarks>
public interface IDwellRegistrar
{
    /// <summary>
    /// Registers a dwell target in the given category.
    /// </summary>
    /// <param name="id">Unique identifier for this target within its category.</param>
    /// <param name="category">UI section the target belongs to.</param>
    /// <param name="radiusPhysical">
    /// Hit-test radius in physical screen pixels.
    /// Use <c>DpiHelper.LogicalToPhysicalX</c> to convert from logical pixels.
    /// </param>
    /// <param name="dwellMs">Time the gaze must remain inside the radius to activate, in milliseconds.</param>
    /// <param name="onActivated">
    /// Callback invoked when dwell completes.
    /// For <see cref="DwellCategory.Modal"/> targets the built-in Yes/No logic runs first
    /// (invoking the <c>onYes</c> action supplied to <c>ShowModal</c> and hiding the dialog);
    /// <paramref name="onActivated"/> is then invoked as an optional extension point.
    /// </param>
    void Register(string id, DwellCategory category, double radiusPhysical, int dwellMs, Action onActivated);

    /// <summary>
    /// Removes a previously registered dwell target.
    /// </summary>
    /// <param name="id">Identifier supplied during <see cref="Register"/>.</param>
    /// <param name="category">Category the target was registered under.</param>
    void Unregister(string id, DwellCategory category);

    /// <summary>
    /// Updates the screen-space center of a registered target.
    /// Must be called every frame (e.g. from a rendering loop) because
    /// WPF overlay windows can reposition their contents between frames.
    /// </summary>
    /// <param name="id">Identifier of the target to update.</param>
    /// <param name="category">Category the target was registered under.</param>
    /// <param name="center">New center position in physical screen pixels.</param>
    void UpdatePosition(string id, DwellCategory category, PhysicalPoint center);

    /// <summary>
    /// Returns the current dwell progress for a target, in the range [0.0, 1.0].
    /// Returns 0 if the gaze is not inside the target's radius or if the target
    /// does not exist.
    /// </summary>
    /// <param name="id">Identifier of the target to query.</param>
    /// <param name="category">Category the target was registered under.</param>
    double GetProgress(string id, DwellCategory category);
}
