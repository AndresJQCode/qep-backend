namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// Spec 2026-11-18 §8: la sección <c>Siigo</c>. <see cref="PartnerId"/> es el <c>Partner-Id</c> de QEP ante Siigo, el mismo para todas las
/// empresas de la integración (3 a 100 alfanuméricos, sin espacios), y viaja en toda llamada; lo valida <c>SiigoOptionsValidator</c>.
/// No es secreto: va en el ConfigMap. La sección la lee también el probador de conexiones de Integrations
/// (<c>SiigoTesterOptions</c>), que no ve este módulo.
/// </summary>
public sealed class SiigoOptions
{
    public const string SectionName = "Siigo";

    public const string DefaultBaseUrl = "https://api.siigo.com";

    /// <summary>Vacía vale la de producción de Siigo.</summary>
    public string? BaseUrl { get; set; } = DefaultBaseUrl;

    public string? PartnerId { get; set; }
}
