namespace Modules.Customers.Domain;

/// <summary>Los códigos de <see cref="CustomersDomainException"/> que otro código del módulo necesita comparar. Un
/// código que sólo se lanza y se muestra sigue como literal donde se lanza.</summary>
public static class CustomersErrorCodes
{
    public const string Merged = "customers.customer.merged";
    public const string Inactive = "customers.customer.inactive";

    /// <summary>Spec de ciudades 2026-10-26 §4.6: la ciudad del cliente ya no es la que se iba a corregir.</summary>
    public const string CityChanged = "customers.customer.city_changed";

    public const string ClassificationNotFound = "customers.customer.classification_not_found";
    public const string CityNotFound = "customers.customer.city_not_found";
    public const string IdentificationTaken = "customers.customer.identification_taken";
}
