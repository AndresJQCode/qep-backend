namespace Modules.Geography.Application;

/// <summary>
/// Spec 2026-10-26 D-C7: lo que la consola de ciudades deja en <c>audit.entries</c>, siempre en el tenant operador, con el operador
/// como actor <c>Human</c>. Lo que el sinónimo corrige en cada tenant lo audita Shopify en el tenant del pedido o cliente (S9).
/// </summary>
public static class GeographyAuditActions
{
    public const string SynonymCreated = "geography.city_synonym.created";
    public const string SynonymCityChanged = "geography.city_synonym.city_changed";
    public const string SynonymDeleted = "geography.city_synonym.deleted";
    public const string SynonymCorrectionsRetried = "geography.city_synonym.corrections_retried";
    public const string UnresolvedCityDiscarded = "geography.unresolved_city.discarded";
    public const string UnresolvedCityRestored = "geography.unresolved_city.restored";
    public const string BackfillRan = "geography.city_backfill.ran";

    public const string SynonymResourceType = "city_synonym";
    public const string UnresolvedCityResourceType = "unresolved_city";
}
