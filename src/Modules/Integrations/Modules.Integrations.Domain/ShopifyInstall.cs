namespace Modules.Integrations.Domain;

/// <summary>
/// Spec 2026-10-15 §4.3 y §9: una instalación de Shopify en curso, en <c>integrations.shopify_installs</c>. Nace en el
/// inicio (A, desde QEP con tenant; B, desde Shopify sin tenant) con un <see cref="State"/> aleatorio de un solo uso y
/// vence a los <see cref="StartLifetime"/>. El canje del <c>code</c> le pone el token cifrado y la tienda, y le da
/// <see cref="ExchangedLifetime"/> para que la persona entre, se registre o espere a que QCode active el módulo (D-S16).
/// El enlace la borra. No tiene tenant hasta el enlace: <see cref="InitiatedTenantId"/> sólo dice desde dónde se
/// inició.
/// </summary>
public sealed class ShopifyInstall
{
    public const int ShopDomainMaxLength = 64;

    /// <summary>32 bytes en base64url sin relleno son 43 caracteres; la columna deja margen.</summary>
    public const int StateMaxLength = 64;

    public const int RedirectUriMaxLength = 512;

    public const int ShopIdMaxLength = 64;

    public const int ShopNameMaxLength = 255;

    public const int ScopesMaxLength = 512;

    /// <summary>El <c>state</c> de una fila reclamada para el canje. El punto no es base64url, así que ningún
    /// <c>state</c> emitido por el inicio puede chocar con uno reclamado.</summary>
    public const string ClaimedStatePrefix = "claimed.";

    public static readonly TimeSpan StartLifetime = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan ExchangedLifetime = TimeSpan.FromDays(7);

    private ShopifyInstall()
    {
        ShopDomain = string.Empty;
        State = string.Empty;
        RedirectUri = string.Empty;
    }

    public Guid Id { get; private set; }

    public string ShopDomain { get; private set; }

    public string State { get; private set; }

    /// <summary>El <c>redirect_uri</c> con el que se pidió la autorización: el canje lo exige idéntico (§12).</summary>
    public string RedirectUri { get; private set; }

    /// <summary>El tenant del inicio A; <c>null</c> en el B. El enlace exige que coincida (<c>install_tenant_mismatch</c>).</summary>
    public Guid? InitiatedTenantId { get; private set; }

    /// <summary>La membresía que inició (A); <c>null</c> en el B.</summary>
    public Guid? InitiatedBy { get; private set; }

    public string? ShopId { get; private set; }

    public string? ShopName { get; private set; }

    /// <summary>Los scopes concedidos, separados por coma, como los devuelve Shopify.</summary>
    public string? Scopes { get; private set; }

    /// <summary>El token offline cifrado con <c>ISecretProtector</c>; <c>null</c> hasta el canje.</summary>
    public byte[]? ProtectedToken { get; private set; }

    /// <summary>Spec 2026-11-12 §6.1: el refresh token cifrado, con AAD <c>installId + "refreshToken"</c>; <c>null</c> hasta el
    /// canje, y en una fila canjeada antes de este cambio.</summary>
    public byte[]? ProtectedRefreshToken { get; private set; }

    /// <summary>Cuándo vence el token de acceso (1 hora tras el canje o el refresco).</summary>
    public DateTimeOffset? AccessTokenExpiresAt { get; private set; }

    /// <summary>Cuándo vence el refresh token (90 días tras emitirse).</summary>
    public DateTimeOffset? RefreshTokenExpiresAt { get; private set; }

    /// <summary>La llave que cifró <see cref="ProtectedToken"/> y <see cref="ProtectedRefreshToken"/>: los dos se sellan
    /// siempre juntos (D4), así que una sola columna basta; el worker de re-cifrado la recorre.</summary>
    public string? KeyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsExchanged => ProtectedToken is not null;

    /// <summary>Inicio A (<paramref name="initiatedTenantId"/> y <paramref name="initiatedBy"/>) o B (los dos en
    /// <c>null</c>). El dominio ya viene normalizado y el <c>redirect_uri</c>, permitido: los valida la aplicación.</summary>
    public static ShopifyInstall Start(
        Guid id,
        string shopDomain,
        string state,
        string redirectUri,
        Guid? initiatedTenantId,
        Guid? initiatedBy,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shopDomain);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        if (shopDomain.Length > ShopDomainMaxLength || state.Length > StateMaxLength || redirectUri.Length > RedirectUriMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(shopDomain), "A Shopify install value exceeds its column.");
        }

        if (initiatedTenantId.HasValue != initiatedBy.HasValue)
        {
            throw new ArgumentException("An install started from QEP carries both the tenant and the membership; one from Shopify, neither.");
        }

        return new ShopifyInstall
        {
            Id = id,
            ShopDomain = shopDomain,
            State = state,
            RedirectUri = redirectUri,
            InitiatedTenantId = initiatedTenantId,
            InitiatedBy = initiatedBy,
            CreatedAt = now,
            ExpiresAt = now + StartLifetime,
        };
    }

    /// <summary>Ya la reclamó un canje: el <c>state</c> de Shopify dejó de existir.</summary>
    public bool IsClaimed => State.StartsWith(ClaimedStatePrefix, StringComparison.Ordinal);

    /// <summary>
    /// El canje reclama la fila <b>antes</b> de llamar a Shopify, reemplazando el <c>state</c> por
    /// <paramref name="claimState"/> (aleatorio, con <see cref="ClaimedStatePrefix"/>). Es lo que lo vuelve de un solo uso:
    /// otro request con el mismo <c>state</c> ya no encuentra la fila, aunque Shopify falle después. El repositorio lo
    /// guarda con un <c>UPDATE</c> condicionado al <c>state</c> anterior.
    /// </summary>
    public void Claim(string claimState, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimState);
        if (!claimState.StartsWith(ClaimedStatePrefix, StringComparison.Ordinal) || claimState.Length > StateMaxLength)
        {
            throw new ArgumentException("A claim state carries the claimed prefix and fits the column.", nameof(claimState));
        }

        if (IsClaimed || !CanBeExchanged(now))
        {
            throw new InvalidOperationException("The install was already claimed, exchanged or expired.");
        }

        State = claimState;
    }

    /// <summary>Vigente y todavía sin token: el <c>state</c> es de un solo uso.</summary>
    public bool CanBeExchanged(DateTimeOffset now) => !IsExchanged && now < ExpiresAt;

    /// <summary>
    /// Spec 2026-11-12 §6.1: la instalación vive 7 días y su token, 1 hora. El enlace refresca el token de la instalación
    /// antes de usarlo si le queda <see cref="ShopifyTokenRefresh.Margin"/> o menos. Una fila sin vencimiento (canjeada antes
    /// de este cambio) no se refresca: no tiene refresh token y el enlace la rechaza.
    /// </summary>
    public bool NeedsTokenRefresh(DateTimeOffset now) =>
        AccessTokenExpiresAt is { } expiresAt && expiresAt - now <= ShopifyTokenRefresh.Margin;

    /// <summary>El canje: guarda la tienda, los dos tokens cifrados y sus vencimientos, y mueve el vencimiento de la
    /// instalación a +7 días desde <paramref name="now"/>.</summary>
    public void MarkExchanged(
        string shopId,
        string shopName,
        string scopes,
        ProtectedSecret token,
        ProtectedSecret refreshToken,
        DateTimeOffset accessTokenExpiresAt,
        DateTimeOffset refreshTokenExpiresAt,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shopId);
        ArgumentNullException.ThrowIfNull(shopName);
        ArgumentNullException.ThrowIfNull(scopes);
        EnsureSameKey(token, refreshToken);
        // El nombre sólo se muestra y se recorta; el id y los scopes no se pueden recortar sin mentir. El gateway ya los
        // rechaza antes (ShopifyGatewayException Invalid): esto es la última defensa contra un 500 de la base.
        if (shopId.Length > ShopIdMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(shopId), "The Shopify shop id exceeds its column.");
        }

        if (scopes.Length > ScopesMaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(scopes), "The Shopify scopes exceed their column.");
        }

        if (!CanBeExchanged(now))
        {
            throw new InvalidOperationException("The install was already exchanged or expired.");
        }

        ShopId = shopId;
        ShopName = shopName.Length > ShopNameMaxLength ? shopName[..ShopNameMaxLength] : shopName;
        Scopes = scopes;
        ProtectedToken = token.Ciphertext;
        ProtectedRefreshToken = refreshToken.Ciphertext;
        KeyId = token.KeyId;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        RefreshTokenExpiresAt = refreshTokenExpiresAt;
        ExpiresAt = now + ExchangedLifetime;
    }

    /// <summary>Spec 2026-11-12 §6.1: el par que devolvió un refresco. Shopify rota los dos tokens a la vez; la instalación
    /// conserva su propio vencimiento (<see cref="ExpiresAt"/>).</summary>
    public void ApplyRefreshedToken(
        ProtectedSecret token,
        ProtectedSecret refreshToken,
        DateTimeOffset accessTokenExpiresAt,
        DateTimeOffset refreshTokenExpiresAt)
    {
        EnsureSameKey(token, refreshToken);
        if (!IsExchanged)
        {
            throw new InvalidOperationException("Only an exchanged install carries a token to refresh.");
        }

        ProtectedToken = token.Ciphertext;
        ProtectedRefreshToken = refreshToken.Ciphertext;
        KeyId = token.KeyId;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        RefreshTokenExpiresAt = refreshTokenExpiresAt;
    }

    /// <summary>Los mismos tokens, cifrados con otra llave (el worker de re-cifrado, §9: una fila canjeada vive hasta 7
    /// días). <paramref name="refreshToken"/> es <c>null</c> sólo en una fila canjeada antes de que existiera. No cambia
    /// los vencimientos ni nada más.</summary>
    public void Reprotect(ProtectedSecret token, ProtectedSecret? refreshToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (refreshToken is not null)
        {
            EnsureSameKey(token, refreshToken);
        }

        if (!IsExchanged)
        {
            throw new InvalidOperationException("Only an exchanged install carries a token to re-encrypt.");
        }

        if (refreshToken is null && ProtectedRefreshToken is not null)
        {
            throw new ArgumentException("An install with a refresh token re-encrypts both tokens with the same key.", nameof(refreshToken));
        }

        ProtectedToken = token.Ciphertext;
        ProtectedRefreshToken = refreshToken?.Ciphertext;
        KeyId = token.KeyId;
    }

    private static void EnsureSameKey(ProtectedSecret token, ProtectedSecret refreshToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(refreshToken);
        if (!string.Equals(token.KeyId, refreshToken.KeyId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Both Shopify tokens of an install are sealed with the same key.", nameof(refreshToken));
        }
    }

    // Sin token ni state: el ToString por defecto de una clase no imprime nada, pero se deja explícito.
    public override string ToString() => $"ShopifyInstall {{ Id = {Id}, ShopDomain = {ShopDomain}, ExpiresAt = {ExpiresAt:O} }}";
}
