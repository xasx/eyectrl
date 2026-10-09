namespace EyeCtrl.Supervisor.Supervision;

using EyeCtrl.Core.Localization;

/// <summary>
/// The Supervisor tray tooltip's line: the prefix plus the state phrase, composed from a table
/// resolved only at the tooltip's display moments — a state change, a hover or a menu open —
/// never per supervision input (T06 review-01 F2: the heartbeat path must do no settings read).
/// This is T05b's resolve-at-display pattern, applied to the tooltip.
/// </summary>
/// <remarks>
/// The tooltip shows on hover, not continuously, so a language change that arrives while the
/// state is steady is picked up by <see cref="Refresh"/> at the next hover; a same-state
/// <see cref="Update"/> is free.
/// </remarks>
internal sealed class TrayStatusText
{
    private readonly Func<StringTable> _resolve;
    private SupervisorState? _state;
    private string? _text;

    /// <param name="resolve">Resolves the Supervisor's table; called only when a line is actually
    /// (re)composed, never on a same-state update.</param>
    public TrayStatusText(Func<StringTable> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _resolve = resolve;
    }

    /// <summary>The line for <paramref name="state"/>, composing (and resolving the table) only
    /// when the state differs from the last rendered one. Called from the supervision loop on
    /// every input, so the steady-heartbeat case does no IO at all.</summary>
    /// <param name="state">The current supervision state.</param>
    /// <returns>The tooltip line.</returns>
    public string Update(SupervisorState state)
    {
        if (_state != state || _text is null)
        {
            _state = state;
            _text = Compose(_resolve(), state);
        }

        return _text;
    }

    /// <summary>Re-resolves the table and recomposes for the last rendered state — the tooltip's
    /// display moments (hover, menu open), so a language change shows without a state change.
    /// Returns null before the first <see cref="Update"/>: the icon still shows its product-name
    /// default, and a hover must not invent a state line.</summary>
    /// <returns>The recomposed line, or null when no state has been rendered yet.</returns>
    public string? Refresh()
    {
        if (_state is null) return null;
        _text = Compose(_resolve(), _state.Value);
        return _text;
    }

    private static string Compose(StringTable strings, SupervisorState state) =>
        SupervisorStateText.TrayPrefix(strings) + SupervisorStateText.Describe(strings, state);
}
