namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

/// <summary>
/// Persistence contract for the user-configurable Quick Actions toolbar.
/// Implementations load and save the collection of <see cref="QuickAction"/> entries
/// to a backing store (typically a JSON file in the application data directory).
/// </summary>
public interface IQuickActionsStore
{
    /// <summary>
    /// Loads every quick action from the backing store.
    /// </summary>
    /// <returns>
    /// A read-only list of all stored quick actions; an empty list if the store is
    /// missing, empty, or unreadable.
    /// </returns>
    IReadOnlyList<QuickAction> LoadAll();

    /// <summary>
    /// Persists the supplied quick actions, replacing any previously stored entries.
    /// </summary>
    /// <param name="actions">The complete set of quick actions to store.</param>
    void Save(IReadOnlyList<QuickAction> actions);

    /// <summary>
    /// Looks up a single quick action by its unique identifier.
    /// </summary>
    /// <param name="id">The <see cref="QuickAction.Id"/> to match.</param>
    /// <returns>The matching <see cref="QuickAction"/>, or <c>null</c> if no action has the given id.</returns>
    QuickAction? FindById(string id);
}
