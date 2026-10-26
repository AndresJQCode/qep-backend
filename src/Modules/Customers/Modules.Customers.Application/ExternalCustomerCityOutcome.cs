namespace Modules.Customers.Application;

/// <summary>Spec de ciudades 2026-10-26 §4.6: qué pasó con el cliente de una venta externa cuando Shopify le pidió
/// completarlo o corregirle la ciudad. <c>Applied</c> y <c>AlreadyApplied</c> llevan la versión que quedó;
/// <c>NotEligible</c> y <c>NotCompletable</c> llevan el motivo.</summary>
public enum ExternalCustomerCityOutcomeKind
{
    Applied,
    AlreadyApplied,
    NotFound,

    /// <summary>El cliente ya no está como lo dejó la importación (fusionado, inactivo, editado…): no se toca.</summary>
    NotEligible,

    /// <summary>Los datos de la venta no alcanzan para una ficha completa: no se toca.</summary>
    NotCompletable,
}

public sealed record ExternalCustomerCityOutcome(ExternalCustomerCityOutcomeKind Kind, long? Version, string? Reason);

/// <summary>Los motivos de <see cref="ExternalCustomerCityOutcomeKind.NotEligible"/>.</summary>
public static class ExternalCustomerCityReasons
{
    public const string Merged = "merged";
    public const string Inactive = "inactive";
    public const string Complete = "complete";
    public const string Incomplete = "incomplete";
    public const string Edited = "edited";
    public const string CityChanged = "city_changed";
}
