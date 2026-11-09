namespace Modules.Quotations.Domain;

/// <summary>
/// Los datos de una parte tal como entran al agregado (<see cref="Quotation.Create"/> /
/// <see cref="Quotation.UpdateDetails"/>). Value object, misma relación con
/// <see cref="QuotationParty"/> que <c>CustomerContactInfo</c> con <c>Customer</c>: la entidad
/// expone los campos planos, esto es la forma de entrada y quien normaliza.
/// </summary>
public sealed record QuotationPartyDetails
{
    public string? Name { get; init; }

    /// <summary>El documento de identidad de a quién se le factura cuando no es el cliente. Sólo
    /// tiene sentido en la parte de facturación: <see cref="QuotationParty"/> lo descarta en la de
    /// entrega, y <see cref="Quotation"/> lo exige cuando la facturación trae nombre propio.</summary>
    public string? IdentificationNumber { get; init; }

    /// <summary>Natural o jurídica, sólo en la parte de facturación (spec 2026-10-26):
    /// <see cref="QuotationParty"/> lo descarta en la de entrega.</summary>
    public PartyPersonType? PersonType { get; init; }

    /// <summary>Uno de los siete códigos (NIT, CC, CE, PASAPORTE, NIF, NIE, TAX_ID; qué país acepta cuáles lo dice <c>DocumentTypesByCountry</c>): el tipo del <see cref="IdentificationNumber"/>. Misma
    /// suerte que el número: sólo existe en facturación y se exige donde el número se exige.</summary>
    public PartyIdentificationType? IdentificationType { get; init; }

    public string? Phone { get; init; }

    public string? Email { get; init; }

    public string? Address { get; init; }

    public Guid? DepartmentId { get; init; }

    public Guid? CityId { get; init; }

    /// <summary>País ISO 3166-1 alfa-2. <c>null</c> es Colombia (<c>CO</c>), que es lo que tienen todas las
    /// filas guardadas antes de la columna y lo que sigue mandando Shopify. Un país distinto trae la
    /// ciudad escrita (<see cref="CityName"/>) y ningún id de DIVIPOLA.</summary>
    public string? Country { get; init; }

    /// <summary>La ciudad como texto, sólo fuera de Colombia: no hay catálogo contra el cual validarla.</summary>
    public string? CityName { get; init; }

    public const string ColombiaCountryCode = "CO";

    public const int CountryLength = 2;

    public const int CityNameMaxLength = 120;

    // Mismos topes que Customer para los campos que son el mismo dato: una parte es una copia
    // puntual de la ficha del cliente, no puede aceptar más de lo que la ficha acepta.
    public const int NameMaxLength = 255;

    public const int PhoneMaxLength = 32;

    public const int EmailMaxLength = 254;

    public const int AddressMaxLength = 255;

    // Mismo tope que CustomerIdentification.NumberMaxLength (Modules.Customers.Domain): es el
    // mismo dato para otra persona. Duplicado y no referenciado porque Quotations no referencia
    // el dominio de Customers; si allá cambia, cambia acá también.
    public const int IdentificationNumberMaxLength = 32;

    /// <summary>Colombia por omisión: <c>null</c>, vacío y <c>CO</c> son la misma parte.</summary>
    public bool IsColombian =>
        string.IsNullOrWhiteSpace(Country)
        || string.Equals(Country.Trim(), ColombiaCountryCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>El carril de la ciudad, igual que en la libreta de direcciones: Colombia lleva
    /// departamento y ciudad de DIVIPOLA, el resto el nombre de la ciudad. Completa si están los dos.</summary>
    public bool HasCompleteCity => IsColombian
        ? DepartmentId is not null && CityId is not null
        : !string.IsNullOrWhiteSpace(CityName);

    internal QuotationPartyDetails Normalized(string fieldPrefix)
    {
        var country = NormalizeCountry(Country);
        var normalized = this with { Country = country };
        normalized.EnsureLane(fieldPrefix);
        return normalized.NormalizedFields(country);
    }

    // Se rechaza en vez de limpiar el campo que sobra: un id con un país de afuera (o una ciudad escrita
    // con CO) es una contradicción de quien escribe, y descartarla en silencio guardaría otra ciudad.
    private void EnsureLane(string fieldPrefix)
    {
        if (IsColombian && !string.IsNullOrWhiteSpace(CityName))
        {
            throw LaneInvalid(
                $"{fieldPrefix}.CityName",
                "A Colombian party takes its city from the department and city ids, not from a city name.");
        }

        if (!IsColombian && (DepartmentId is not null && DepartmentId != Guid.Empty
            || CityId is not null && CityId != Guid.Empty))
        {
            throw LaneInvalid(
                $"{fieldPrefix}.CityId",
                "A party outside Colombia takes its city as text, not from the department and city ids.");
        }
    }

    private static QuotationsDomainException LaneInvalid(string field, string message) =>
        new("quotations.party.city_lane_invalid", message,
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    // CO se guarda como null: una sola forma para Colombia, así las filas nuevas son iguales a las viejas y
    // la igualdad de registros (Shopify compara partes) no depende de cómo se escribió el país.
    private static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var code = value.Trim().ToUpperInvariant();
        if (code.Length != CountryLength || !code.All(char.IsAsciiLetterUpper))
        {
            throw new QuotationsDomainException(
                "quotations.party.country_invalid",
                "The party country must be an ISO 3166-1 alpha-2 code.");
        }

        return code == ColombiaCountryCode ? null : code;
    }

    private QuotationPartyDetails NormalizedFields(string? country) => new()
    {
        Country = country,
        CityName = NormalizeOptional(
            CityName,
            CityNameMaxLength,
            "quotation.party.city_name_too_long",
            $"The party city name cannot exceed {CityNameMaxLength} characters."),
        Name = NormalizeOptional(
            Name,
            NameMaxLength,
            "quotation.party.name_too_long",
            $"The party name cannot exceed {NameMaxLength} characters."),
        // Se recorta y nada más, mismo criterio que CustomerIdentification: los puntos y guiones
        // de un NIT ("900.123.456-1") son como la persona lo escribe.
        IdentificationNumber = NormalizeOptional(
            IdentificationNumber,
            IdentificationNumberMaxLength,
            "quotation.party.identification_number_too_long",
            $"The party identification number cannot exceed {IdentificationNumberMaxLength} characters."),
        PersonType = PersonType,
        IdentificationType = IdentificationType,
        Phone = NormalizeOptional(
            Phone,
            PhoneMaxLength,
            "quotation.party.phone_too_long",
            $"The party phone cannot exceed {PhoneMaxLength} characters."),
        Email = NormalizeOptional(
            Email,
            EmailMaxLength,
            "quotation.party.email_too_long",
            $"The party email cannot exceed {EmailMaxLength} characters."),
        Address = NormalizeOptional(
            Address,
            AddressMaxLength,
            "quotation.party.address_too_long",
            $"The party address cannot exceed {AddressMaxLength} characters."),
        DepartmentId = NormalizeOptionalId(DepartmentId),
        CityId = NormalizeOptionalId(CityId)
    };

    // Vacio y ausente son lo mismo para un campo opcional, mismo criterio que
    // CustomerContactInfo.NormalizeOptional.
    private static string? NormalizeOptional(
        string? value, int maxLength, string tooLongCode, string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new QuotationsDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }

    // Guid.Empty llega de un formulario que mandó el select vacío: es "sin elegir", no un id.
    private static Guid? NormalizeOptionalId(Guid? value) =>
        value is null || value == Guid.Empty ? null : value;
}

/// <summary>
/// Las dos partes de una cotización tal como entran al agregado. <b>Null es el caso normal</b>:
/// "esta cotización factura (o entrega) a los datos del cliente", que es exactamente lo que
/// muestra el switch encendido de la UI. <see cref="Quotation.UpdateDetails"/> reemplaza el
/// recurso entero, así que un null que antes tenía fila la borra.
/// </summary>
public sealed record QuotationParties(
    QuotationPartyDetails? Billing,
    QuotationPartyDetails? Shipping,
    /// <summary>
    /// Con los datos del cliente (sin fila de facturación), a cuál de sus dos nombres se le
    /// factura: el de contacto o la razón social (CLI-RS-01). Un cliente mayorista es una empresa
    /// y factura a su razón social, pero el contacto sigue siendo la persona con la que se habla.
    ///
    /// No aplica cuando la facturación tiene datos propios: ahí el nombre lo escribió alguien.
    /// </summary>
    bool BillingUsesBusinessName = false,
    /// <summary>
    /// El cliente pasa a recoger el pedido por la tienda en vez de recibirlo en una dirección.
    /// <b>Gana sobre <see cref="Shipping"/></b>: con esto prendido no hay a dónde entregar, así que
    /// una parte de entrega que llegue igual se descarta. La facturación no se entera.
    /// </summary>
    bool IsStorePickup = false,
    /// <summary>
    /// La factura sale a nombre de consumidor final (<see cref="FinalConsumer"/>). Excluye a
    /// <see cref="Billing"/> y a <see cref="BillingUsesBusinessName"/>: a diferencia de
    /// <see cref="IsStorePickup"/>, que descarta la parte de entrega, acá el agregado rechaza la
    /// combinación — son dos nombres distintos para la misma factura y no hay uno que gane.
    /// </summary>
    bool BillsToFinalConsumer = false,
    /// <summary>
    /// Si quien recibe la factura —cuando <see cref="Billing"/> trae datos propios— practica
    /// retención en la fuente. Con los datos del cliente, ese hecho lo dice el cliente
    /// (<c>Customer.WithRetention</c>, ver <see cref="Quotation.CustomerWithRetention"/>); con
    /// datos propios nadie más lo sabe, así que hay que preguntarlo — <c>null</c> mientras no se
    /// contestó no es "no aplica", es "todavía no se sabe" (ver <see cref="Quotation.EnsureConvertibleToOrder"/>).
    /// Se ignora cuando <see cref="Billing"/> es null: ahí manda el cliente.
    /// </summary>
    bool? BillingWithRetention = null,
    /// <summary>Mismo criterio que <see cref="BillingWithRetention"/> pero para el excedente de
    /// IVA (<c>Customer.VatSurplus</c> / <see cref="Quotation.CustomerVatSurplus"/>).</summary>
    bool? BillingVatSurplus = null)
{
    /// <summary>Las dos partes tomadas del cliente: ninguna fila. El estado por defecto de una
    /// cotización nueva.</summary>
    public static QuotationParties Empty { get; } = new(null, null);
}
