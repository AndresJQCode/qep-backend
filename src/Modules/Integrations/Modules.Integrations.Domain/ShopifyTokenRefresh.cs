using System.Globalization;

namespace Modules.Integrations.Domain;

/// <summary>
/// Spec 2026-11-12 §6.2: las reglas del refresco del token offline de Shopify, que vive 1 hora. El worker corre cada
/// <see cref="Interval"/> y refresca cuando al token le queda <see cref="Margin"/> o menos: con tokens de 1 hora, eso deja
/// tres vueltas de margen ante una falla transitoria.
/// </summary>
public static class ShopifyTokenRefresh
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(20);

    /// <summary>El formato de <see cref="ShopifyFieldKeys.TokenExpiresAt"/> y <see cref="ShopifyFieldKeys.RefreshTokenExpiresAt"/>:
    /// ISO 8601 de ida y vuelta, en UTC, como <c>InstagramFieldKeys.TokenExpiresAt</c>.</summary>
    public static string Format(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Sin fecha legible se refresca: es mejor un refresco de más que un token vencido sin aviso (D14).</summary>
    public static bool IsDue(string? tokenExpiresAt, DateTimeOffset now) =>
        !TryParse(tokenExpiresAt, out var expiresAt) || expiresAt - now <= Margin;

    /// <summary>Sin fecha legible, el token no se da por vencido: una conexión anterior a este cambio conserva su token
    /// hasta que el worker la marque (D14, §6.3).</summary>
    public static bool IsExpired(string? tokenExpiresAt, DateTimeOffset now) =>
        TryParse(tokenExpiresAt, out var expiresAt) && now >= expiresAt;

    /// <summary>Los únicos estados de los que el refresco no puede sacar a una conexión (§6.2): la persona tiene que volver a
    /// abrir la app desde Shopify. Una pausada o en <c>NeedsAttention</c> por otra causa se sigue refrescando.</summary>
    public static bool IsUnrecoverable(ConnectionStatus status, string? lastFailureCode) =>
        status == ConnectionStatus.NeedsAttention
        && (string.Equals(lastFailureCode, ConnectionFailureCodes.TokenExpired, StringComparison.Ordinal)
            || string.Equals(lastFailureCode, ConnectionFailureCodes.AppUninstalled, StringComparison.Ordinal));

    private static bool TryParse(string? value, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);
}
