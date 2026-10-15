namespace Modules.Integrations.Application;

/// <summary>
/// Lo fijo de Business Login for Instagram (spec 2026-10-14 §3): la ventana de autorización y los dos permisos
/// sin los cuales no hay bandeja. El SPA arma la URL con <see cref="AuthorizeUrl"/>, el <c>appId</c> del catálogo,
/// <see cref="RequiredScopes"/> y su propio <c>redirect_uri</c>; el backend canjea el <c>code</c> (decisión 2).
/// </summary>
public static class InstagramLogin
{
    public const string AuthorizeUrl = "https://www.instagram.com/oauth/authorize";

    public const string ScopeBasic = "instagram_business_basic";

    public const string ScopeManageMessages = "instagram_business_manage_messages";

    public static readonly IReadOnlyList<string> RequiredScopes = [ScopeBasic, ScopeManageMessages];

    /// <summary>Metadato de la auditoría de la conexión creada por este flujo (como <c>path:</c>/<c>event:</c> en WhatsApp).</summary>
    public const string OnboardingAuditField = "onboarding:instagram_login";

    /// <summary>Los campos que se suscriben por cuenta (V-2): los DM, el visto, los botones y las reacciones.</summary>
    public const string WebhookFields = "messages,messaging_seen,messaging_postbacks,messaging_reactions";

    /// <summary>Lo que Meta devuelve en <c>account_type</c> para una cuenta profesional.</summary>
    public static bool IsProfessional(string? accountType) =>
        string.Equals(accountType, "BUSINESS", StringComparison.Ordinal)
        || string.Equals(accountType, "MEDIA_CREATOR", StringComparison.Ordinal);

    public static bool HasRequiredScopes(IReadOnlyCollection<string> granted) =>
        RequiredScopes.All(scope => granted.Contains(scope, StringComparer.Ordinal));
}

/// <summary>
/// Lo público del caso de uso Instagram de la app de Meta (spec 2026-10-14 §9, V-1 cerrada: tiene su propio
/// <c>AppId</c> y <c>AppSecret</c>, distintos de <c>Meta:App</c>). <see cref="IsConfigured"/> sigue a D-M3: sin la
/// sección (sólo fuera de Production), <c>instagram</c> no sale en el catálogo y el canje responde
/// <c>code_exchange_failed</c>. <see cref="IsAllowedRedirect"/> es la lista blanca de orígenes del
/// <c>redirect_uri</c> (§11). El secreto nunca pasa por acá.
/// </summary>
public interface IInstagramAppSettings
{
    bool IsConfigured { get; }

    string? AppId { get; }

    string GraphApiVersion { get; }

    /// <summary>El origen (esquema, host y puerto) del <paramref name="redirectUri"/> está permitido.</summary>
    bool IsAllowedRedirect(Uri redirectUri);
}

/// <summary>El token corto (1 hora) que devuelve el canje del <c>code</c>, con el id de la cuenta y los permisos que
/// la persona concedió. <see cref="ToString"/> no imprime el token.</summary>
public sealed record InstagramShortLivedToken(string AccessToken, string UserId, IReadOnlyList<string> Permissions)
{
    public override string ToString() =>
        $"InstagramShortLivedToken {{ UserId = {UserId}, Permissions = [{string.Join(",", Permissions)}] }}";
}

/// <summary>Un token largo (60 días) con lo que le queda de vida. <see cref="ToString"/> no imprime el token.</summary>
public sealed record InstagramLongLivedToken(string AccessToken, TimeSpan ExpiresIn)
{
    public override string ToString() => $"InstagramLongLivedToken {{ ExpiresIn = {ExpiresIn} }}";
}

/// <summary><c>GET /me?fields=id,username,name,account_type</c>.</summary>
public sealed record InstagramAccount(string Id, string? Username, string? Name, string? AccountType);

/// <summary>
/// Spec 2026-10-14 §8.1 contra <c>api.instagram.com</c> y <c>graph.instagram.com</c>. El adaptador vive en
/// Infrastructure sobre <c>integrations.instagram-graph</c>. Nunca registra el <c>code</c>, los tokens ni el
/// <c>client_secret</c>.
/// </summary>
public interface IInstagramLoginGateway
{
    /// <summary>POST https://api.instagram.com/oauth/access_token (form) → token corto, user_id y permisos.</summary>
    Task<GraphResult<InstagramShortLivedToken>> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken);

    /// <summary>GET /access_token?grant_type=ig_exchange_token → token largo de 60 días.</summary>
    Task<GraphResult<InstagramLongLivedToken>> ExchangeLongLivedAsync(string shortLivedToken, CancellationToken cancellationToken);

    /// <summary>GET /refresh_access_token?grant_type=ig_refresh_token → otro token largo de 60 días.</summary>
    Task<GraphResult<InstagramLongLivedToken>> RefreshAsync(string longLivedToken, CancellationToken cancellationToken);

    /// <summary>GET /{v}/me?fields=id,username,name,account_type.</summary>
    Task<GraphResult<InstagramAccount>> GetMeAsync(string accessToken, CancellationToken cancellationToken);

    /// <summary>POST /{v}/{igUserId}/subscribed_apps?subscribed_fields=… (V-2).</summary>
    Task<GraphResult<bool>> SubscribeAsync(string igUserId, string accessToken, CancellationToken cancellationToken);
}
