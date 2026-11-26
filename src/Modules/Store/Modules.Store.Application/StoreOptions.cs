namespace Modules.Store.Application;

/// <summary>La sección <c>Store</c> de appsettings. Se valida al arrancar, como <c>LoansOptions</c>.</summary>
public sealed class StoreOptions
{
    public const string SectionName = "Store";

    /// <summary>Dominio raíz del que cuelga el host de cada tienda: <c>{slug}.{RootDomain}</c> (spec §5.3).</summary>
    public string RootDomain { get; set; } = "lulocrm.com";

    /// <summary>Tope de pedidos que una tienda recibe por día: frena el spam que pasó el rate limit por IP.</summary>
    public int DailyRequestCap { get; set; } = 500;

    public StorefrontOptions Storefront { get; set; } = new();

    public TurnstileOptions Turnstile { get; set; } = new();

    /// <summary>
    /// <paramref name="requireSecrets"/> es true sólo en Production: fuera de ahí los secretos viven en user-secrets o faltan, y exigirlos
    /// tumbaría el arranque de todo host de pruebas. Un secreto presente siempre se valida. <paramref name="turnstileMayBeDisabled"/> es
    /// true sólo en Development y Testing y va aparte: apagar el desafío deja pasar cualquier token, y eso no depende de los secretos.
    /// </summary>
    public IReadOnlyList<string> Validate(bool requireSecrets = true, bool turnstileMayBeDisabled = false)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(RootDomain))
        {
            failures.Add("Store:RootDomain is required.");
        }

        if (DailyRequestCap <= 0)
        {
            failures.Add("Store:DailyRequestCap must be greater than zero.");
        }

        var secret = Storefront.SharedSecret;
        if (string.IsNullOrEmpty(secret) ? requireSecrets : secret.Length < StorefrontOptions.MinimumSecretLength)
        {
            failures.Add(
                $"Store:Storefront:SharedSecret must be at least {StorefrontOptions.MinimumSecretLength} characters.");
        }

        if (Turnstile.Enabled && requireSecrets && string.IsNullOrWhiteSpace(Turnstile.SecretKey))
        {
            failures.Add("Store:Turnstile:SecretKey is required when Store:Turnstile:Enabled is true.");
        }

        // El pasa-todo existe para Development y Testing; fuera de ellos (Staging incluido) apagar el desafío dejaría el envío público
        // sin anti-bot.
        if (!Turnstile.Enabled && !turnstileMayBeDisabled)
        {
            failures.Add("Store:Turnstile:Enabled cannot be false outside Development and Testing.");
        }

        return failures;
    }
}

/// <summary>El secreto compartido con el servidor de la tienda pública (storefront), que firma las llamadas públicas.</summary>
public sealed class StorefrontOptions
{
    public const int MinimumSecretLength = 32;

    public string? SharedSecret { get; set; }
}

/// <summary>Cloudflare Turnstile en el envío de solicitudes.</summary>
public sealed class TurnstileOptions
{
    public bool Enabled { get; set; } = true;

    public string? SecretKey { get; set; }

    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
}
