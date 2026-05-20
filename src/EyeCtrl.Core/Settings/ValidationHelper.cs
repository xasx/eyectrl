namespace EyeCtrl.Core.Settings;

/// <summary>
/// Helper class for common validation rules.
/// </summary>
public static class ValidationHelper
{
    /// <summary>
    /// Validates that a value is within the specified range.
    /// </summary>
    public static string? ValidateRange<T>(T value, T min, T max, string propertyName) where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
        {
            return $"{propertyName} must be between {min} and {max}, but was {value}";
        }
        return null;
    }

    /// <summary>
    /// Validates that a string value is in the allowed set.
    /// </summary>
    public static string? ValidateStringEnum(string value, string[] allowedValues, string propertyName)
    {
        if (!allowedValues.Contains(value))
        {
            return $"{propertyName} must be one of [{string.Join(", ", allowedValues)}], but was '{value}'";
        }
        return null;
    }

    /// <summary>
    /// Validates that an enum value is defined.
    /// </summary>
    public static string? ValidateEnumDefined<TEnum>(TEnum value, string propertyName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            return $"{propertyName} must be a defined enum value of {typeof(TEnum).Name}, but was {value}";
        }
        return null;
    }

    /// <summary>
    /// Validates that a color string is valid hex format.
    /// </summary>
    public static string? ValidateHexColor(string value, string propertyName)
    {
        if (string.IsNullOrEmpty(value) ||
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"^#[0-9A-Fa-f]{6}$"))
        {
            return $"{propertyName} must be a valid hex color (e.g., #00FF00), but was '{value}'";
        }
        return null;
    }

    /// <summary>
    /// Collects validation results, filtering out null values.
    /// </summary>
    public static List<string> CollectErrors(params string?[] validationResults)
    {
        return validationResults.Where(r => r != null).Cast<string>().ToList();
    }
}
