using BuildingBlocks.Domain.Identification;

namespace Modules.Customers.Domain;

public readonly record struct CustomerBillingProfileId(Guid Value)
{
    public static CustomerBillingProfileId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Una tasa de retención de un perfil de facturación: sólo el id, como <see cref="CustomerTaxRate"/>, con el tenant
/// repetido para el índice «en uso».
/// </summary>
public sealed record CustomerBillingProfileTaxRate(Guid TenantId, Guid TaxRateId);

/// <summary>Los códigos de la libreta de perfiles de facturación (spec 2026-11-24 §3.5). Todos 422.</summary>
public static class CustomerBillingProfileErrors
{
    public const string NameRequired = "customers.billing_profile.name_required";
    public const string NameTooLong = "customers.billing_profile.name_too_long";
    public const string IdentificationRequired = "customers.billing_profile.identification_required";
    public const string IdentificationTooLong = "customers.billing_profile.identification_too_long";
    public const string IdentificationTypeInvalid = "customers.billing_profile.identification_type_invalid";
    public const string DocumentTypeNotAllowed = "customers.billing_profile.document_type_not_allowed";
    public const string PersonTypeInvalid = "customers.billing_profile.person_type_invalid";
    public const string PersonTypeRequiresNit = "customers.billing_profile.person_type_requires_nit";
    public const string PhoneTooLong = "customers.billing_profile.phone_too_long";
    public const string EmailInvalid = "customers.billing_profile.email_invalid";
    public const string AddressRequired = "customers.billing_profile.address_required";
    public const string AddressTooLong = "customers.billing_profile.address_too_long";
    public const string CountryInvalid = "customers.billing_profile.country_invalid";
    public const string CityRequired = "customers.billing_profile.city_required";
    public const string CityLaneInvalid = "customers.billing_profile.city_lane_invalid";
    public const string RegionLaneInvalid = "customers.billing_profile.region_lane_invalid";
    public const string LimitReached = "customers.billing_profile.limit_reached";
    public const string NotFound = "customers.billing_profile.not_found";
    public const string PrincipalNotRemovable = "customers.billing_profile.principal_not_removable";
}

/// <summary>
/// A quién se le factura cuando no es el cliente tal cual: otra razón social, otro documento, otro correo, otras
/// retenciones. Entidad hija de <see cref="Customer"/> gemela de <see cref="CustomerAddress"/> (sin repositorio ni
/// construcción fuera del agregado, porque «una sola principal» y el tope son invariantes del cliente). La cotización y el
/// pedido la copian; ningún documento guarda su id. No es el <c>CustomerBillingProfile</c> de ServiceContracts (cobros).
/// </summary>
public sealed class CustomerBillingProfile
{
    /// <summary>Spec 2026-11-24 §3.4: cuántos perfiles puede tener un cliente. Viaja en la respuesta.</summary>
    public const int MaxPerCustomer = 50;

    // Los topes de la parte de facturación de un documento (QuotationPartyDetails): lo que se guarda acá tiene que caber allá.
    public const int NameMaxLength = 255;

    public const int IdentificationNumberMaxLength = 32;

    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 255;

    private readonly List<CustomerBillingProfileTaxRate> _withholdingTaxRates = [];

    // EF Core materializa por aca. El codigo nunca construye la entidad asi: solo el agregado.
    private CustomerBillingProfile()
    {
        Name = string.Empty;
        IdentificationType = string.Empty;
        IdentificationNumber = string.Empty;
        Address = string.Empty;
        Country = Customer.ColombiaCountryCode;
    }

    private CustomerBillingProfile(CustomerBillingProfileId id, CustomerId customerId, DateTimeOffset occurredAt)
        : this()
    {
        Id = id;
        CustomerId = customerId;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public CustomerBillingProfileId Id { get; private set; }

    public CustomerId CustomerId { get; private set; }

    /// <summary>A quién se factura: la razón social o el nombre. No hay alias.</summary>
    public string Name { get; private set; }

    /// <summary>El nombre del contrato (<c>Natural</c> o <c>Juridica</c>); nulo es «no se sabe».</summary>
    public string? PersonType { get; private set; }

    /// <summary>El código de <see cref="DocumentTypesByCountry"/>, permitido para <see cref="Country"/> al escribir.</summary>
    public string IdentificationType { get; private set; }

    public string IdentificationNumber { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string Address { get; private set; }

    /// <summary>ISO-3166-1 alpha-2 en mayúsculas; decide el carril de ciudad, como en <see cref="CustomerAddress"/>.</summary>
    public string Country { get; private set; }

    /// <summary>Sólo en <c>CO</c>.</summary>
    public Guid? CityId { get; private set; }

    /// <summary>La ciudad escrita, sólo fuera de <c>CO</c>.</summary>
    public string? CityName { get; private set; }

    /// <summary>La región, sólo fuera de <c>CO</c>.</summary>
    public Guid? RegionId { get; private set; }

    public IReadOnlyCollection<Guid> WithholdingTaxRateIds =>
        _withholdingTaxRates.Select(rate => rate.TaxRateId).ToArray();

    /// <summary>El excedente de IVA. En el documento puede quedar sin contestar; un perfil ya contestó.</summary>
    public bool VatSurplus { get; private set; }

    /// <summary>La que se propone primero. El agregado garantiza exactamente una mientras haya perfiles.</summary>
    public bool IsPrincipal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    // Los detalles llegan ya normalizados: el agregado los compara antes de decidir si crea o devuelve uno existente.
    internal static CustomerBillingProfile Create(
        CustomerId customerId,
        Guid tenantId,
        CustomerBillingProfileDetails normalized,
        DateTimeOffset occurredAt)
    {
        var profile = new CustomerBillingProfile(CustomerBillingProfileId.New(), customerId, occurredAt);
        profile.Assign(tenantId, normalized, occurredAt);
        return profile;
    }

    internal void Apply(Guid tenantId, CustomerBillingProfileDetails details, DateTimeOffset occurredAt) =>
        Assign(tenantId, details.Normalized(), occurredAt);

    // Solo el agregado decide cual es la principal, como en CustomerAddress.
    internal void MarkPrincipal(bool isPrincipal, DateTimeOffset occurredAt)
    {
        if (IsPrincipal == isPrincipal)
        {
            return;
        }

        IsPrincipal = isPrincipal;
        UpdatedAt = occurredAt;
    }

    private void Assign(Guid tenantId, CustomerBillingProfileDetails normalized, DateTimeOffset occurredAt)
    {
        Name = normalized.Name;
        PersonType = normalized.PersonType;
        IdentificationType = normalized.IdentificationType;
        IdentificationNumber = normalized.IdentificationNumber;
        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
        Country = normalized.Country;
        CityId = normalized.CityId;
        CityName = normalized.CityName;
        RegionId = normalized.RegionId;
        VatSurplus = normalized.VatSurplus;

        // Se quita lo que sobra y se agrega lo que falta, como Customer.SetWithholdingTaxRates: reemplazar la lista entera
        // borraria e insertaria la misma clave en el mismo guardado.
        var wanted = normalized.WithholdingTaxRateIds.ToHashSet();
        _withholdingTaxRates.RemoveAll(rate => !wanted.Contains(rate.TaxRateId));
        wanted.ExceptWith(_withholdingTaxRates.Select(rate => rate.TaxRateId));
        _withholdingTaxRates.AddRange(wanted.Select(id => new CustomerBillingProfileTaxRate(tenantId, id)));

        UpdatedAt = occurredAt;
    }
}

/// <summary>Los datos de un perfil tal como entran al agregado. Value object: valida y normaliza, como
/// <see cref="CustomerAddressDetails"/>.</summary>
public sealed record CustomerBillingProfileDetails
{
    public required string Name { get; init; }

    public string? PersonType { get; init; }

    public required string IdentificationType { get; init; }

    public required string IdentificationNumber { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public required string Address { get; init; }

    /// <summary>Vacío es <c>CO</c>, como en la libreta de direcciones.</summary>
    public string Country { get; init; } = Customer.ColombiaCountryCode;

    public Guid? CityId { get; init; }

    public string? CityName { get; init; }

    public Guid? RegionId { get; init; }

    public IReadOnlyCollection<Guid> WithholdingTaxRateIds { get; init; } = [];

    public bool VatSurplus { get; init; }

    internal CustomerBillingProfileDetails Normalized()
    {
        var country = NormalizeCountry(Country);
        var name = Required(
            Name,
            CustomerBillingProfile.NameMaxLength,
            (CustomerBillingProfileErrors.NameRequired, "The billing profile name is required."),
            CustomerBillingProfileErrors.NameTooLong,
            "name");
        var number = Required(
            IdentificationNumber,
            CustomerBillingProfile.IdentificationNumberMaxLength,
            (CustomerBillingProfileErrors.IdentificationRequired, "The billing profile identification is required."),
            CustomerBillingProfileErrors.IdentificationTooLong,
            "identification");
        var type = NormalizeIdentificationType(IdentificationType, country);
        var personType = NormalizePersonType(PersonType, type, country);
        var phone = NormalizePhone(Phone);
        var email = NormalizeEmail(Email);
        var address = Required(
            Address,
            CustomerBillingProfile.AddressMaxLength,
            (CustomerBillingProfileErrors.AddressRequired, "The billing profile address is required."),
            CustomerBillingProfileErrors.AddressTooLong,
            "address");
        var cityName = NormalizeCityName(CityName);
        var cityId = EnsureCityLane(country, CityId, cityName);
        var regionId = EnsureRegionLane(country, RegionId);

        return new()
        {
            Name = name,
            PersonType = personType,
            IdentificationType = type,
            IdentificationNumber = number,
            Phone = phone,
            Email = email,
            Address = address,
            Country = country,
            CityId = cityId,
            CityName = cityName,
            RegionId = regionId,
            WithholdingTaxRateIds = NormalizeTaxRates(WithholdingTaxRateIds),
            VatSurplus = VatSurplus
        };
    }

    private static string NormalizeCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return Customer.ColombiaCountryCode;
        }

        var trimmed = country.Trim();
        return trimmed.Length == CustomerContactInfo.CountryLength && trimmed.All(char.IsAsciiLetter)
            ? trimmed.ToUpperInvariant()
            : throw new CustomersDomainException(
                CustomerBillingProfileErrors.CountryInvalid,
                $"The billing profile country must be an ISO-3166-1 alpha-2 code ({CustomerContactInfo.CountryLength} letters).");
    }

    private static string Required(
        string? value,
        int maxLength,
        (string Code, string Message) required,
        string tooLongCode,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CustomersDomainException(required.Code, required.Message);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CustomersDomainException(
                tooLongCode, $"The billing profile {field} cannot exceed {maxLength} characters.")
            : trimmed;
    }

    // El tipo de documento se compara con el pais del propio perfil (DocumentTypesByCountry), no con el del cliente.
    private static string NormalizeIdentificationType(string? type, string country)
    {
        var known = DocumentTypesByCountry.All.FirstOrDefault(code =>
            string.Equals(code, type?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new CustomersDomainException(
                CustomerBillingProfileErrors.IdentificationTypeInvalid,
                $"The identification type must be one of {string.Join(", ", DocumentTypesByCountry.All)}.");

        return DocumentTypesByCountry.IsAllowed(known, country)
            ? known
            : throw IdentificationTypeError(
                CustomerBillingProfileErrors.DocumentTypeNotAllowed,
                "The identification type is not allowed for the country.");
    }

    // Misma regla que Customer.EnsurePersonTypeMatches: en Colombia una persona juridica se identifica con NIT.
    private static string? NormalizePersonType(string? personType, string identificationType, string country)
    {
        if (string.IsNullOrWhiteSpace(personType))
        {
            return null;
        }

        var known = PersonTypeParser.SupportedWireValues.FirstOrDefault(value =>
            string.Equals(value, personType.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new CustomersDomainException(
                CustomerBillingProfileErrors.PersonTypeInvalid, "The person type must be Natural or Juridica.");

        if (known == Domain.PersonType.Juridica.ToWireValue()
            && country == Customer.ColombiaCountryCode
            && identificationType != Domain.IdentificationType.Nit.ToWireValue())
        {
            throw IdentificationTypeError(
                CustomerBillingProfileErrors.PersonTypeRequiresNit, "A legal entity is identified by its NIT.");
        }

        return known;
    }

    // Marca IdentificationType, el campo que la persona corrige en el formulario (como en la ficha del cliente).
    private static CustomersDomainException IdentificationTypeError(string code, string message) =>
        new(
            code,
            message,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { ["IdentificationType"] = [message] });

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var trimmed = phone.Trim();
        return trimmed.Length > CustomerBillingProfile.PhoneMaxLength
            ? throw new CustomersDomainException(
                CustomerBillingProfileErrors.PhoneTooLong,
                $"The billing profile phone cannot exceed {CustomerBillingProfile.PhoneMaxLength} characters.")
            : trimmed;
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var trimmed = email.Trim();
        return trimmed.Length <= CustomerBillingProfile.EmailMaxLength && CustomerContactInfo.IsPlausibleEmail(trimmed)
            ? trimmed
            : throw new CustomersDomainException(
                CustomerBillingProfileErrors.EmailInvalid, "The billing profile email is not a valid address.");
    }

    // La ciudad escrita reusa el codigo del cliente: el handoff no nombra uno propio y el validador ya la rechaza por campo.
    private static string? NormalizeCityName(string? cityName)
    {
        if (string.IsNullOrWhiteSpace(cityName))
        {
            return null;
        }

        var trimmed = cityName.Trim();
        return trimmed.Length > CustomerContactInfo.CityNameMaxLength
            ? throw new CustomersDomainException(
                "customers.customer.city_name_too_long",
                $"The billing profile city cannot exceed {CustomerContactInfo.CityNameMaxLength} characters.")
            : trimmed;
    }

    // Los dos carriles de ciudad de CustomerAddressDetails: CO lleva el id DIVIPOLA y nada de texto; el resto, al reves.
    private static Guid? EnsureCityLane(string country, Guid? cityId, string? cityName)
    {
        if (country == Customer.ColombiaCountryCode)
        {
            if (cityId is null || cityId == Guid.Empty)
            {
                throw CityRequired();
            }

            return cityName is null
                ? cityId
                : throw LaneInvalid("A Colombian billing profile uses a city id, not a city name.");
        }

        if (cityId is not null)
        {
            throw LaneInvalid("A billing profile outside Colombia uses a city name, not a city id.");
        }

        return cityName is null ? throw CityRequired() : null;
    }

    private static Guid? EnsureRegionLane(string country, Guid? regionId)
    {
        if (regionId is null || regionId == Guid.Empty)
        {
            return null;
        }

        return country == Customer.ColombiaCountryCode
            ? throw new CustomersDomainException(
                CustomerBillingProfileErrors.RegionLaneInvalid,
                "A Colombian billing profile takes its region from the city, not from a region id.")
            : regionId;
    }

    private static CustomersDomainException CityRequired() =>
        new(CustomerBillingProfileErrors.CityRequired, "The billing profile city is required.");

    private static CustomersDomainException LaneInvalid(string message) =>
        new(CustomerBillingProfileErrors.CityLaneInvalid, message);

    private static Guid[] NormalizeTaxRates(IReadOnlyCollection<Guid>? taxRateIds)
    {
        var ids = (taxRateIds ?? []).Distinct().ToArray();
        return ids.Contains(Guid.Empty)
            ? throw new CustomersDomainException(
                "customers.customer.withholding_tax_rate_invalid", "A withholding tax rate id cannot be empty.")
            : ids;
    }
}
