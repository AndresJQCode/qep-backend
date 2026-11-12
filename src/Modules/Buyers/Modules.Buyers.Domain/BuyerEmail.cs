using System.Diagnostics.CodeAnalysis;

namespace Modules.Buyers.Domain;

public static class BuyerEmail
{
    public const int MaxLength = 254;

    public static bool TryNormalize(string? raw, [NotNullWhen(true)] out string normalized)
    {
        normalized = string.Empty;
        if (raw is null) return false;

        var candidate = raw.Trim().ToLowerInvariant();
        if (candidate.Length == 0 || candidate.Length > MaxLength || !candidate.Contains('@')) return false;

        normalized = candidate;
        return true;
    }
}
