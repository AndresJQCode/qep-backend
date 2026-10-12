namespace Modules.Customers.Domain;

/// <summary>
/// Spec 2026-10-11 decisión 10: quién apagó el marketing de un cliente. <see cref="Manual"/> lo hizo una persona
/// en la ficha; <see cref="Meta"/> lo hizo el sistema cuando Meta respondió 131050 (la persona apagó el marketing
/// de la empresa en WhatsApp); <see cref="Button"/> lo hizo el sistema cuando la persona tocó el botón de respuesta rápida «Detener
/// promociones» de una plantilla (decisión del owner 2026-10-12, P1).
/// </summary>
public enum MarketingConsentSource
{
    Manual,
    Meta,
    Button,
}

public static class MarketingConsentSourceParser
{
    public static string ToWireValue(this MarketingConsentSource source) => source switch
    {
        MarketingConsentSource.Manual => "manual",
        MarketingConsentSource.Meta => "meta",
        MarketingConsentSource.Button => "button",
        _ => throw Invalid(),
    };

    public static MarketingConsentSource Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "MANUAL" => MarketingConsentSource.Manual,
        "META" => MarketingConsentSource.Meta,
        "BUTTON" => MarketingConsentSource.Button,
        _ => throw Invalid(),
    };

    private static CustomersDomainException Invalid() => new(
        "customers.customer.marketing_source_invalid", "The marketing consent source is not one of the supported values.");
}
