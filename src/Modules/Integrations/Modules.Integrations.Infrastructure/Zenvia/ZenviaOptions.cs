namespace Modules.Integrations.Infrastructure.Zenvia;

/// <summary>
/// La sección <c>Integrations:Zenvia</c> (spec 2026-10-08, «Probar la credencial»). Sólo la usa la
/// prueba de la credencial: el sender global de Quotations sigue leyendo <c>Quotations:WhatsApp:BaseUrl</c>
/// hasta el spec del consumidor.
/// </summary>
public sealed class ZenviaOptions
{
    public const string SectionName = "Integrations:Zenvia";

    public string BaseUrl { get; init; } = "https://api.zenvia.com";
}
