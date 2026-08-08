using System.Globalization;
using System.Text;

namespace Core.Models.Common;

/// <summary>
/// Stable cursor for relevance-ordered searches. The score is ordered
/// descending and the id ascending, so inserts between pages cannot reorder
/// already-returned rows.
/// </summary>
public readonly record struct SearchCursor(int Score, long Id)
{
    public string Encode()
    {
        var raw = Score.ToString(CultureInfo.InvariantCulture)
                  + ":"
                  + Id.ToString(CultureInfo.InvariantCulture);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out SearchCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Accept the pre-relevance numeric cursor during rollout. It is
        // treated as the lowest score and remains monotonic for old clients.
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacyId)
            && legacyId > 0)
        {
            cursor = new SearchCursor(int.MinValue, legacyId);
            return true;
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/')
                + new string('=', (4 - value.Length % 4) % 4);
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = raw.Split(':', 2);
            return parts.Length == 2
                   && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)
                   && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                   && id > 0
                   && (cursor = new SearchCursor(score, id)) is var _;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
