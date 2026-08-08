namespace Core.Models;

public static class PhoneNumberNormalizer
{
    /// <summary>
    /// Accepts only an explicit E.164 number. Local numbers are rejected so
    /// two country-specific representations cannot bypass the unique index.
    /// </summary>
    public static string? TryNormalizeE164(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length < 8 || trimmed[0] != '+')
            return null;

        var digitCount = trimmed.Length - 1;
        if (digitCount is < 7 or > 15)
            return null;

        for (var i = 1; i < trimmed.Length; i++)
        {
            if ((uint)(trimmed[i] - '0') > 9)
                return null;
        }

        return trimmed;
    }
}
