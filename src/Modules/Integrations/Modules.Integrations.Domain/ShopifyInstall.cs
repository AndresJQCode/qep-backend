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

    /// <summary>La llave que cifró <see cref="ProtectedToken"/>; el worker de re-cifrado la recorre.</summary>
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

    /// <summary>Vigente y todavía sin token: el <c>state</c> es de un solo uso.</summary>
    public bool CanBeExchanged(DateTimeOffset now) => !IsExchanged && now < ExpiresAt;

    /// <summary>El canje: guarda la tienda y el token cifrado, y mueve el vencimiento a +7 días desde
    /// <paramref name="now"/>.</summary>
    public void MarkExchanged(string shopId, string shopName, string scopes, ProtectedSecret token, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shopId);
        ArgumentNullException.ThrowIfNull(shopName);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(token);
        if (!CanBeExchanged(now))
        {
            throw new InvalidOperationException("The install was already exchanged or expired.");
        }

        ShopId = shopId;
        ShopName = shopName.Length > ShopNameMaxLength ? shopName[..ShopNameMaxLength] : shopName;
        Scopes = scopes;
        ProtectedToken = token.Ciphertext;
        KeyId = token.KeyId;
        ExpiresAt = now + ExchangedLifetime;
    }

    // Sin token ni state: el ToString por defecto de una clase no imprime nada, pero se deja explícito.
    public override string ToString() => $"ShopifyInstall {{ Id = {Id}, ShopDomain = {ShopDomain}, ExpiresAt = {ExpiresAt:O} }}";
}
