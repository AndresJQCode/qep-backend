namespace Modules.Store.Application;

/// <summary>La sección <c>Store</c> de appsettings. Se valida al arrancar, como <c>LoansOptions</c>.</summary>
public sealed class StoreOptions
{
    public const string SectionName = "Store";

    /// <summary>Dominio raíz del que cuelga el host de cada tienda: <c>{slug}.{RootDomain}</c> (spec §5.3).</summary>
    public string RootDomain { get; set; } = "lulocrm.com";

    /// <summary>Cuánto dura el candado con que «Tomar» reserva una solicitud mientras crea el pedido, para que un segundo clic no la duplique.</summary>
    public TimeSpan ConversionLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Tope de solicitudes que una tienda recibe por día: frena el spam que pasó el rate limit por IP.</summary>
    public int DailyRequestCap { get; set; } = 500;

    public StorefrontOptions Storefront { get; set; } = new();

    public TurnstileOptions Turnstile { get; set; } = new();

    /// <summary>
    /// <paramref name="requireSecrets"/> es false en Development y Testing: ahí los secretos viven en user-secrets o faltan, y exigirlos
    /// tumbaría el arranque de todas las demás pruebas de integración. Un secreto presente siempre se valida.
    /// </summary>
    public IReadOnlyList<string> Validate(bool requireSecrets = true)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(RootDomain))
        {
            failures.Add("Store:RootDomain is required.");
        }

        if (ConversionLease <= TimeSpan.Zero)
        {
            failures.Add("Store:ConversionLease must be greater than zero.");
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
