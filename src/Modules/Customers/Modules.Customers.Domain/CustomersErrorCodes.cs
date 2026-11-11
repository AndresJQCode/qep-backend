namespace Modules.Customers.Domain;

/// <summary>Los códigos de <see cref="CustomersDomainException"/> que otro código del módulo necesita comparar. Un
/// código que sólo se lanza y se muestra sigue como literal donde se lanza.</summary>
public static class CustomersErrorCodes
{
    public const string Merged = "customers.customer.merged";
    public const string Inactive = "customers.customer.inactive";

    // Spec 2026-11-10: contactos de empresa. Todos 422.
    public const string CompanyNotFound = "customers.customer.company_not_found";
    public const string CompanyInactive = "customers.customer.company_inactive";
    public const string CompanyIsSelf = "customers.customer.company_is_self";
    public const string CompanyIsContact = "customers.customer.company_is_contact";
    public const string HasContacts = "customers.customer.has_contacts";
    public const string MergeCompanyConflict = "customers.customer.merge_company_conflict";

    /// <summary>Spec de ciudades 2026-10-26 §4.6: la ciudad del cliente ya no es la que se iba a corregir.</summary>
    public const string CityChanged = "customers.customer.city_changed";

    public const string ClassificationNotFound = "customers.customer.classification_not_found";
    public const string CityNotFound = "customers.customer.city_not_found";
    public const string IdentificationTaken = "customers.customer.identification_taken";

    /// <summary>Spec 2026-10-26: una jurídica colombiana se identifica con NIT.</summary>
    public const string PersonTypeRequiresNit = "customers.customer.person_type_requires_nit";

    /// <summary>El tipo de documento no está en la lista del país del cliente.</summary>
    public const string DocumentTypeNotAllowed = "customers.customer.document_type_not_allowed";

    /// <summary>Spec 2026-10-26: el tenant exige el tipo de persona y el cliente no lo trae.</summary>
    public const string PersonTypeRequired = "customers.customer.person_type_required";

    /// <summary>Politica de campos del cliente: el tenant exige el documento al guardar y no viene.</summary>
    public const string IdentificationRequired = "customers.customer.identification_required";

    /// <summary>El documento viene a medias: el tipo sin el numero, o el numero sin el tipo.</summary>
    public const string IdentificationIncomplete = "customers.customer.identification_incomplete";

    /// <summary>Politica de campos del cliente: el tenant exige la ciudad (DIVIPOLA) de un cliente colombiano.</summary>
    public const string CityRequired = "customers.customer.city_required";

    /// <summary>Politica de campos del cliente: el tenant exige la ciudad escrita de un cliente de afuera.</summary>
    public const string CityNameRequired = "customers.customer.city_name_required";
}
