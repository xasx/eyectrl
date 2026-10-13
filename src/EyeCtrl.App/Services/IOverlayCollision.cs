namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.Core.Models;

/// <summary>
/// The D17 collision seam: whether the panel rect would cover another gaze-consuming surface, and
/// when to ask again. Task 10 shipped the show-time gate behind an always-clear stub; Task 11
/// maps <see cref="GazeConsumerInventory"/> here — the real inventory (keyboard, both edge-button
/// bars, the two menus, external dwell) plus the <see cref="Changed"/> trigger set that keeps the
/// check continuous while the panel is up.
/// </summary>
public interface IOverlayCollision
{
    /// <summary>Whether <paramref name="panelRect"/> is clear of every live gaze consumer
    /// (strict overlap — touching edges are clear, Task 1).</summary>
    bool IsClear(PhysicalRect panelRect);

    /// <summary>
    /// Raised after any change that can move, add or remove a gaze consumer: the keyboard's and
    /// the menus' visibility flips, either edge bar's button set, toolbar visibility and toolbar
    /// settings edits, display-geometry changes, keyboard-edge toggles and external-dwell
    /// activations. The preview service re-runs its collision check on every raise while the
    /// panel is up. The pre-Task-11 stub never raised it.
    /// </summary>
    event Action? Changed;
}
