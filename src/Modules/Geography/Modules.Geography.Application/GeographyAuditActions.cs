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

    // Spec 2026-11-26: la consola del exterior. El payload lleva país, texto normalizado e ids, nunca datos de clientes.
    public const string ForeignSynonymCreated = "geography.foreign_city_synonym.created";
    public const string ForeignSynonymCityChanged = "geography.foreign_city_synonym.city_changed";
    public const string ForeignSynonymDeleted = "geography.foreign_city_synonym.deleted";
    public const string UnresolvedForeignCityDiscarded = "geography.unresolved_foreign_city.discarded";
    public const string UnresolvedForeignCityRestored = "geography.unresolved_foreign_city.restored";

    public const string SynonymResourceType = "city_synonym";
    public const string UnresolvedCityResourceType = "unresolved_city";
    public const string BackfillResourceType = "city_backfill";
    public const string ForeignSynonymResourceType = "foreign_city_synonym";
    public const string UnresolvedForeignCityResourceType = "unresolved_foreign_city";
}
