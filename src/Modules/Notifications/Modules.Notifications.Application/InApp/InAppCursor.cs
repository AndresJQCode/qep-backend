using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Notifications.Application;

/// <summary>
/// Spec 2026-11-22 §8.1: the opaque cursor is the base64url of <c>{createdAt:O}|{id:D}</c>, the last entry of the previous page. Pagination
/// is by cursor and not by offset because entries arrive while the person scrolls, and an offset would repeat or skip rows.
/// </summary>
internal static class InAppCursor
{
    public static string Encode(DateTimeOffset createdAt, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}|{id:D}"));

    public static bool TryParse(string? cursor, out (DateTimeOffset CreatedAt, Guid Id) value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('|');
            if (parts.Length != 2
                || !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt)
                || !Guid.TryParseExact(parts[1], "D", out var id))
            {
                return false;
            }

            // Npgsql refuses an offset other than zero for timestamptz, and a cursor is client input: normalize it here.
            value = (createdAt.ToUniversalTime(), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// <c>upTo</c> of read-all (spec §8.4): ISO 8601 <b>with</b> a zone. A date without one would be read in the server's zone, and the screen
/// only sends back what it received, so a missing zone is a mistake of the caller.
/// </summary>
internal static partial class InAppUpTo
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex WithZone();

    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is not null
            && WithZone().IsMatch(text)
            && TryParseUtc(text, out value);
    }

    // The regex accepts «+05:00», and Npgsql refuses any offset but zero for timestamptz (a 500): the instant is the same in UTC.
    private static bool TryParseUtc(string text, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            value = parsed.ToUniversalTime();
            return true;
        }

        value = default;
        return false;
    }
}

/// <summary>
/// Postgres keeps <c>timestamptz</c> in microseconds and .NET in 100 ns ticks. A time the handler writes and returns without reloading
/// would differ from what the next read gives back, and the screen sends <c>createdAt</c>/<c>readAt</c> back as received (spec §8.4).
/// </summary>
internal static class InAppTime
{
    public static DateTimeOffset ToMicroseconds(DateTimeOffset value) => new(value.UtcTicks / 10 * 10, TimeSpan.Zero);
}
