namespace EyeCtrl.Core.Localization;

/// <summary>
/// One key's text in one language: either a single string or the two plural forms
/// (<c>one</c>/<c>other</c>) a <c>{one, other}</c> entry in the table JSON carries.
/// </summary>
internal sealed record StringTableText
{
    private StringTableText(string? value, string? one, string? other)
    {
        Value = value;
        One = one;
        Other = other;
    }

    /// <summary>The single string, or null when this is a plural entry.</summary>
    public string? Value { get; }

    /// <summary>The <c>one</c> form of a plural entry, or null when this is a single string.</summary>
    public string? One { get; }

    /// <summary>The <c>other</c> form of a plural entry, or null when this is a single string.</summary>
    public string? Other { get; }

    /// <summary>Whether this entry carries plural forms rather than one string.</summary>
    public bool IsPlural => Value is null;

    /// <summary>Whether every form this entry uses carries non-blank text.</summary>
    public bool IsComplete => IsPlural
        ? !string.IsNullOrWhiteSpace(One) && !string.IsNullOrWhiteSpace(Other)
        : !string.IsNullOrWhiteSpace(Value);

    /// <summary>Creates a single-string entry.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The entry.</returns>
    public static StringTableText Singular(string value) => new(value, null, null);

    /// <summary>Creates a plural entry.</summary>
    /// <param name="one">The singular form.</param>
    /// <param name="other">The plural form.</param>
    /// <returns>The entry.</returns>
    public static StringTableText Plural(string one, string other) => new(null, one, other);
}
