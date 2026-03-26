namespace EyeCtrl.Core.Settings;

/// <summary>
/// Interface for settings objects that can validate their state.
/// </summary>
public interface IValidatable
{
    /// <summary>
    /// Validates the current state of the object.
    /// </summary>
    /// <returns>A list of validation errors, or an empty list if valid.</returns>
    IReadOnlyList<string> Validate();
}
