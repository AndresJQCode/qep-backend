using BuildingBlocks.Domain.Identification;

namespace Modules.Companies.Domain;

/// <summary>
/// Una empresa del tenant: la contraparte con la que se factura y se cotiza. Guarda solo los datos
/// maestros vivos; un documento congela su propia copia de a quien se le emitio.
///
/// Sus cuentas bancarias son una coleccion (<see cref="CompanyBankAccount"/>) desde EMP-08. Antes
/// era un unico <c>AccountNumber</c> plano, unico por tenant. Ese cambio no contradice a
/// <c>RF-091</c> —"Registrar los datos de la empresa, incluido el numero de cuenta", que no dice
/// ni unico ni uno solo—, sino a la unicidad por tenant que este modulo habia agregado de mas.
/// </summary>
public sealed class Company
{
    // Espejan los anchos de columna de companies.companies. Guardar aca significa que un valor
    // demasiado largo falla como 422 con codigo de dominio en vez de llegar a PostgreSQL y
    // volver como 500 server.unexpected. Los dos salen del schema del formulario que ya existe
    // en el frontend (features/companies/types/company-form.schema.ts). El del numero de cuenta
    // se mudo con la columna: vive en CompanyBankAccount.
    public const int NameMaxLength = 160;

    public const int TaxIdMaxLength = 32;

    public const int CountryLength = 2;

    public const int CityNameMaxLength = 120;

    public const string ColombiaCountryCode = "CO";

    private readonly List<CompanyBankAccount> _bankAccounts = [];
    private readonly List<CompanyTaxRate> _withholdingTaxRates = [];

    // EF Core materializa por aca. El codigo nunca construye el agregado asi: Create es el unico
    // punto de entrada, y es el que hace cumplir los invariantes.
    private Company()
    {
        Name = string.Empty;
        TaxId = string.Empty;
        Country = string.Empty;
    }

    private Company(
        CompanyId id,
        Guid tenantId,
        string name,
        IReadOnlyCollection<CompanyBankAccount> bankAccounts,
        string taxId,
        string country,
        CompanyDocumentType documentType,
        Guid? cityId,
        string? cityName,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt,
        bool chargesVat,
        Guid? regionId)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        TaxId = taxId;
        Country = country;
        DocumentType = documentType;
        CityId = cityId;
        CityName = cityName;
        RegionId = regionId;
        _bankAccounts.AddRange(bankAccounts);
        Apply(contact);
        ChargesVat = chargesVat;
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public CompanyId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// Las cuentas bancarias de la empresa, en el orden en que se cargaron. **Pueden ser cero**
    /// (spec 2026-11-17 §4.1): una empresa nueva todavía no tiene cuenta, y un cobro por tarjeta o
    /// pasarela no la necesita. El tope es <see cref="CompanyBankAccount.MaxPerCompany"/>.
    ///
    /// La unicidad vive dentro de la empresa —la misma terna banco/numero/moneda no se carga dos
    /// veces— y no en un indice de base. Es invariante del agregado: la coleccion entera se
    /// valida en memoria antes de tocar nada, asi que no hace falta que PostgreSQL arbitre ni que
    /// la unidad de trabajo traduzca un 23505. Por eso <c>IX_companies_tenant_account_number</c>
    /// desaparecio en EMP-08 en vez de mudarse a la tabla hija: la regla que hacia cumplir —dos
    /// empresas distintas no comparten numero de cuenta— no salia de ningun requisito.
    /// </summary>
    public IReadOnlyList<CompanyBankAccount> BankAccounts => _bankAccounts;

    /// <summary>
    /// Las tasas municipales (ReteICA) que la empresa aplica, una por municipio y tipo (spec ReteICA por empresa §7.3). Que
    /// cada id sea una tasa municipal activa del país de la empresa lo valida Application contra Catalog; el agregado sólo
    /// guarda el conjunto.
    /// </summary>
    public IReadOnlyCollection<Guid> WithholdingTaxRateIds =>
        _withholdingTaxRates.Select(rate => rate.TaxRateId).ToArray();

    /// <summary>
    /// Número del documento, cuyo tipo es <see cref="DocumentType"/>. **No es unico todavia.** Un
    /// número repetido en dos empresas del mismo tenant puede ser un error de datos o una
    /// sucursal — nadie lo decidio. Si el gate del modulo lo cierra en "unico", es un indice
    /// unico **con su propio codigo de dominio**.
    /// </summary>
    public string TaxId { get; private set; }

    /// <summary>País de la empresa: código ISO 3166-1 alfa-2 en mayúsculas.</summary>
    public string Country { get; private set; }

    /// <summary>Tipo del documento de <see cref="TaxId"/>; depende del país.</summary>
    public CompanyDocumentType DocumentType { get; private set; }

    /// <summary>
    /// FK a <c>geography.cities(id)</c>, tipada como <see cref="Guid"/> plano y no como el
    /// <c>CityId</c> de Geography — mismo motivo que <c>Customer.CityId</c>: ningún módulo de
    /// dominio de este repo referencia el dominio de otro, así que este agregado no puede
    /// nombrar un tipo que vive en <c>Modules.Geography.Domain</c>. Que la fila exista la
    /// garantiza la FK de base (agregada a mano en la migración, ver
    /// <c>CompaniesDbContext.ConfigureCompany</c>) más la resolución previa contra
    /// <c>ICompanyGeographyLookup</c> en el handler — acá sólo se comprueba que no venga vacía.
    /// Sólo para Colombia; nula en el extranjero.
    /// </summary>
    public Guid? CityId { get; private set; }

    /// <summary>Ciudad escrita a mano, sólo para empresas fuera de Colombia; nula en Colombia.</summary>
    public string? CityName { get; private set; }

    /// <summary>
    /// La región (provincia) del domicilio, referencia blanda a <c>geography.regions</c> (sin FK entre esquemas, igual que
    /// <see cref="CityId"/>). Sólo para una empresa de un país con catálogo de regiones distinto de Colombia; nula en
    /// Colombia, que deriva el departamento de <see cref="CityId"/>. Que la región exista y sea del país lo comprueba el
    /// handler contra Geography; el dominio sólo garantiza el carril.
    /// </summary>
    public Guid? RegionId { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Si la empresa cobra IVA (spec 2026-11-17 §5.1). Lo que emite una empresa que no lo cobra va
    /// al 0 % en líneas y envío, y el cliente paga el precio de lista. Cada documento congela su
    /// propia copia al elegir la empresa: cambiarlo acá no toca lo ya emitido.
    /// </summary>
    public bool ChargesVat { get; private set; } = true;

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista, como en <c>Product</c>, <c>Tenant</c> y <c>Membership</c>.
    /// Cada mutacion lo incrementa, y la infraestructura lo mapea con <c>IsConcurrencyToken()</c>,
    /// de modo que el <c>UPDATE</c> lleve la version leida en su <c>WHERE</c>.
    ///
    /// Sin el, dos escrituras que se solapan se pisan en silencio: la segunda no solo pierde la
    /// primera, sino que puede dejar la empresa editada **despues** de inactivarse, porque
    /// <see cref="EnsureActive"/> se evalua contra la copia en memoria del que escribe y no
    /// contra el estado real al momento del commit. Lo encontraron los lentes de fiabilidad y
    /// resiliencia en la revision de CAT-02, sobre el mismo patron.
    /// </summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Company Create(
        CompanyId id,
        Guid tenantId,
        string name,
        IReadOnlyCollection<CompanyBankAccount> bankAccounts,
        string taxId,
        string country,
        CompanyDocumentType documentType,
        Guid? cityId,
        string? cityName,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt,
        bool chargesVat = true,
        Guid? regionId = null)
    {
        var normalizedCountry = NormalizeCountry(country);
        EnsureDocumentTypeAllowed(normalizedCountry, documentType);
        var normalizedTaxId = NormalizeTaxId(taxId);
        EnsureTaxIdIsNumeric(documentType, normalizedTaxId);
        var location = EnsureValidLocation(normalizedCountry, cityId, cityName, regionId);

        return new Company(
            id,
            tenantId,
            NormalizeName(name),
            NormalizeBankAccounts(bankAccounts),
            normalizedTaxId,
            normalizedCountry,
            documentType,
            location.CityId,
            location.CityName,
            contact,
            occurredAt,
            chargesVat,
            location.RegionId);
    }

    /// <summary>
    /// Reemplaza el recurso entero, coleccion de cuentas incluida: lo que no viene en el cuerpo se
    /// **quita**. <paramref name="chargesVat"/> es obligatorio acá: decidir qué hace un cuerpo sin
    /// el campo (conservar) es del handler, no del agregado.
    /// </summary>
    public void Update(
        string name,
        IReadOnlyCollection<CompanyBankAccount> bankAccounts,
        string taxId,
        string country,
        CompanyDocumentType documentType,
        Guid? cityId,
        string? cityName,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt,
        bool chargesVat,
        Guid? regionId = null)
    {
        EnsureActive();

        // Todo se normaliza a locales **antes** de asignar nada. Asignar campo por campo mientras
        // se valida deja el agregado a medio escribir cuando el tercero falla: el 422 le dice al
        // llamador que no se guardo nada, pero la instancia en memoria ya tiene el nombre nuevo, y
        // esa es la que EF persiste en el siguiente SaveChanges de la misma unidad de trabajo.
        // Con la coleccion el sintoma es peor —una empresa sin ninguna cuenta— porque vaciarla es
        // parte de reemplazarla.
        var normalizedName = NormalizeName(name);
        var normalizedAccounts = NormalizeBankAccounts(bankAccounts);
        var normalizedTaxId = NormalizeTaxId(taxId);
        var normalizedCountry = NormalizeCountry(country);
        EnsureDocumentTypeAllowed(normalizedCountry, documentType);
        EnsureTaxIdIsNumeric(documentType, normalizedTaxId);
        var location = EnsureValidLocation(normalizedCountry, cityId, cityName, regionId);
        var normalizedContact = contact.Normalized();

        Name = normalizedName;
        TaxId = normalizedTaxId;
        Country = normalizedCountry;
        DocumentType = documentType;
        CityId = location.CityId;
        CityName = location.CityName;
        RegionId = location.RegionId;
        _bankAccounts.Clear();
        _bankAccounts.AddRange(normalizedAccounts);
        Assign(normalizedContact);
        ChargesVat = chargesVat;
        Version++;
        UpdatedAt = occurredAt;
    }

    // Asigna los tres siempre, incluidos los null. Se puede **limpiar** un campo, no solo
    // setearlo: una implementacion que ignore los null "para no pisar" deja campos imborrables y
    // pasa todas las demas pruebas. Por eso UpdateClearsTheOptionalFieldsThatArriveNull existe.
    private void Apply(CompanyContactInfo contact) => Assign(contact.Normalized());

    private void Assign(CompanyContactInfo normalized)
    {
        Phone = normalized.Phone;
        Email = normalized.Email;
        Address = normalized.Address;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        if (!IsActive)
        {
            throw new CompaniesDomainException(
                "companies.company.already_inactive",
                "The company is already inactive.");
        }

        IsActive = false;
        Version++;
        UpdatedAt = occurredAt;
    }

    // La vuelta de Deactivate. Sin ella una empresa inactiva seria terminal, porque Update abre
    // con EnsureActive() y ningun otro metodo devuelve IsActive a true. Es la falta que CAT-07
    // tuvo que corregir en producto despues, y que aca nace cubierta.
    //
    // Desde EMP-08 tampoco hay nada que revalidar al reactivar: la unicidad del numero de cuenta
    // dejo de ser global al tenant, asi que desactivar nunca "libero" un numero que reactivar
    // pudiera encontrar tomado.
    public void Activate(DateTimeOffset occurredAt)
    {
        if (IsActive)
        {
            throw new CompaniesDomainException(
                "companies.company.already_active",
                "The company is already active.");
        }

        IsActive = true;
        Version++;
        UpdatedAt = occurredAt;
    }

    /// <summary>
    /// Reemplaza las tasas municipales de la empresa. Deduplica; <see cref="Guid.Empty"/> se rechaza. Sube la versión y
    /// devuelve <c>true</c> sólo si el conjunto cambió: guardar lo mismo no es un cambio (mismo criterio que
    /// <c>Customer.SetWithholdingTaxRates</c>).
    /// </summary>
    public bool SetWithholdingTaxRates(IReadOnlyCollection<Guid> taxRateIds, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(taxRateIds);
        if (taxRateIds.Any(id => id == Guid.Empty))
        {
            throw new CompaniesDomainException(
                "companies.company.withholding_tax_rate_invalid",
                "A withholding tax rate id cannot be empty.");
        }

        var wanted = taxRateIds.ToHashSet();
        if (wanted.SetEquals(_withholdingTaxRates.Select(rate => rate.TaxRateId)))
        {
            return false;
        }

        _withholdingTaxRates.RemoveAll(rate => !wanted.Contains(rate.TaxRateId));
        var kept = _withholdingTaxRates.Select(rate => rate.TaxRateId).ToHashSet();
        foreach (var id in wanted.Where(id => !kept.Contains(id)))
        {
            _withholdingTaxRates.Add(new CompanyTaxRate(TenantId, id));
        }

        Version++;
        UpdatedAt = occurredAt;
        return true;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new CompaniesDomainException(
                "companies.company.inactive",
                "An inactive company cannot be edited.");
        }
    }

    /// <summary>
    /// Normaliza cada cuenta y hace cumplir los invariantes de la coleccion: el tope y que no se
    /// repitan. Cero cuentas es válido (spec 2026-11-17 §4.1).
    ///
    /// El orden importa. Los duplicados se buscan sobre las cuentas **ya normalizadas**, porque
    /// sobre las crudas " CTA-1" y "CTA-1" pasan como dos. Y el tope se comprueba antes de
    /// normalizar una por una: si llegan diez mil, rechazar en O(1) es mejor que recortar diez mil
    /// cadenas para terminar rechazando igual.
    /// </summary>
    private static List<CompanyBankAccount> NormalizeBankAccounts(
        IReadOnlyCollection<CompanyBankAccount> bankAccounts)
    {
        if (bankAccounts.Count > CompanyBankAccount.MaxPerCompany)
        {
            throw new CompaniesDomainException(
                "companies.company.bank_accounts_too_many",
                $"A company cannot have more than {CompanyBankAccount.MaxPerCompany} bank accounts.");
        }

        var normalized = new List<CompanyBankAccount>(bankAccounts.Count);
        var seen = new HashSet<(string, string, string)>();

        foreach (var account in bankAccounts.Select(account => account.Normalized()))
        {
            if (!seen.Add(account.DeduplicationKey()))
            {
                throw new CompaniesDomainException(
                    "companies.company.bank_account_duplicated",
                    "The same bank account is listed more than once.");
            }

            normalized.Add(account);
        }

        return normalized;
    }

    // Recortar espacios es parte del invariante, no higiene del llamador: sin recortar, " Andes" y
    // "Andes" son dos nombres distintos para cualquier comparacion, cosa que nadie leyendo la
    // lista haria.
    private static string NormalizeName(string name) =>
        Normalize(
            name,
            NameMaxLength,
            "companies.company.name_required",
            "The company name is required.",
            "companies.company.name_too_long",
            $"The company name cannot exceed {NameMaxLength} characters.");

    private static string NormalizeTaxId(string taxId) =>
        Normalize(
            taxId,
            TaxIdMaxLength,
            "companies.company.tax_id_required",
            "The company tax id is required.",
            "companies.company.tax_id_too_long",
            $"The company tax id cannot exceed {TaxIdMaxLength} characters.");

    // NIT, CC y CE solo llevan dígitos (con punto, guion y espacio); un NIE, un pasaporte o un TAX_ID pueden
    // llevar letras. Se bloquea siempre, también un número viejo que se reenvía sin cambios: vive solo en el
    // alta y la edición, nunca al leer del repositorio, así que una empresa ya guardada con letras se sigue leyendo.
    private static void EnsureTaxIdIsNumeric(CompanyDocumentType documentType, string taxId)
    {
        if (!DocumentNumberFormat.IsValidFor(documentType.ToWire(), taxId))
        {
            throw new CompaniesDomainException(
                "companies.company.tax_id_not_numeric",
                DocumentNumberFormat.NumericTypesMessage);
        }
    }

    // Dos letras ASCII, en mayúsculas. No se valida contra la lista ISO: el catálogo de países es
    // del frontend y el backend solo necesita que el código tenga la forma de uno.
    private static string NormalizeCountry(string country)
    {
        var trimmed = country?.Trim().ToUpperInvariant() ?? string.Empty;

        return trimmed.Length == CountryLength && trimmed.All(c => c is >= 'A' and <= 'Z')
            ? trimmed
            : throw new CompaniesDomainException(
                "companies.company.country_invalid",
                "The country must be a two-letter ISO code.");
    }

    private static void EnsureDocumentTypeAllowed(string country, CompanyDocumentType documentType)
    {
        if (!CompanyDocumentTypes.AllowedFor(country).Contains(documentType))
        {
            throw new CompaniesDomainException(
                "companies.company.document_type_not_allowed",
                $"The document type {documentType.ToWire()} is not allowed for country {country}.");
        }
    }

    // Una empresa colombiana se queda sin CityName y una de afuera sin CityId: guardar los dos
    // dejaría dos respuestas a «dónde está» sin nada que diga cuál gana. Para Colombia el chequeo
    // es mínimo (no vacía): que la fila exista es del handler, que resuelve contra
    // ICompanyGeographyLookup antes de llegar acá — el dominio no puede llamar a Geography.
    // Mismo criterio que Supplier.EnsureValidLocation.
    private static (Guid? CityId, string? CityName, Guid? RegionId) EnsureValidLocation(
        string country,
        Guid? cityId,
        string? cityName,
        Guid? regionId)
    {
        // Guid.Empty es un id roto del cliente HTTP, no una región: cuenta como ausente.
        var normalizedRegion = regionId == Guid.Empty ? null : regionId;

        if (country == ColombiaCountryCode)
        {
            // Red del dominio: el validador ya rechaza la región con CO con un error por campo.
            if (normalizedRegion is not null)
            {
                throw new CompaniesDomainException(
                    "companies.company.region_lane_invalid",
                    "A Colombian company takes its region from the city, not from a region id.");
            }

            return cityId is { } id && id != Guid.Empty
                ? (id, null, null)
                : throw new CompaniesDomainException(
                    "companies.company.city_required",
                    "The city is required.");
        }

        var trimmed = cityName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new CompaniesDomainException(
                "companies.company.city_name_required",
                "The city name is required for a company outside Colombia.");
        }

        return trimmed.Length > CityNameMaxLength
            ? throw new CompaniesDomainException(
                "companies.company.city_name_too_long",
                $"The city name cannot exceed {CityNameMaxLength} characters.")
            : (null, trimmed, normalizedRegion);
    }

    private static string Normalize(
        string value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CompaniesDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CompaniesDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }
}
