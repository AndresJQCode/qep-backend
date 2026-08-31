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

    private readonly List<CompanyBankAccount> _bankAccounts = [];

    // EF Core materializa por aca. El codigo nunca construye el agregado asi: Create es el unico
    // punto de entrada, y es el que hace cumplir los invariantes.
    private Company()
    {
        Name = string.Empty;
        TaxId = string.Empty;
    }

    private Company(
        CompanyId id,
        Guid tenantId,
        string name,
        IReadOnlyCollection<CompanyBankAccount> bankAccounts,
        string taxId,
        Guid cityId,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        TaxId = taxId;
        CityId = cityId;
        _bankAccounts.AddRange(bankAccounts);
        Apply(contact);
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public CompanyId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>
    /// Las cuentas bancarias de la empresa, en el orden en que se cargaron. **Al menos una.**
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
    /// NIT. **No es unico todavia.** Un NIT repetido en dos empresas del mismo tenant puede ser un
    /// error de datos o una sucursal — nadie lo decidio. Si el gate del modulo lo cierra en
    /// "unico", es un indice unico **con su propio codigo de dominio**.
    /// </summary>
    public string TaxId { get; private set; }

    /// <summary>
    /// FK a <c>geography.cities(id)</c>, tipada como <see cref="Guid"/> plano y no como el
    /// <c>CityId</c> de Geography — mismo motivo que <c>Customer.CityId</c>: ningún módulo de
    /// dominio de este repo referencia el dominio de otro, así que este agregado no puede
    /// nombrar un tipo que vive en <c>Modules.Geography.Domain</c>. Que la fila exista la
    /// garantiza la FK de base (agregada a mano en la migración, ver
    /// <c>CompaniesDbContext.ConfigureCompany</c>) más la resolución previa contra
    /// <c>ICompanyGeographyLookup</c> en el handler — acá sólo se comprueba que no venga vacía.
    /// </summary>
    public Guid CityId { get; private set; }

    public bool IsActive { get; private set; }

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
        Guid cityId,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt) =>
        new(
            id,
            tenantId,
            NormalizeName(name),
            NormalizeBankAccounts(bankAccounts),
            NormalizeTaxId(taxId),
            EnsureValidCityId(cityId),
            contact,
            occurredAt);

    /// <summary>
    /// Reemplaza el recurso entero, coleccion de cuentas incluida: lo que no viene en el cuerpo se
    /// **quita**.
    /// </summary>
    public void Update(
        string name,
        IReadOnlyCollection<CompanyBankAccount> bankAccounts,
        string taxId,
        Guid cityId,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt)
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
        var normalizedCityId = EnsureValidCityId(cityId);
        var normalizedContact = contact.Normalized();

        Name = normalizedName;
        TaxId = normalizedTaxId;
        CityId = normalizedCityId;
        _bankAccounts.Clear();
        _bankAccounts.AddRange(normalizedAccounts);
        Assign(normalizedContact);
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
    /// Normaliza cada cuenta y hace cumplir los invariantes de la coleccion: cuantas hay y que no
    /// se repitan.
    ///
    /// El orden importa. Los duplicados se buscan sobre las cuentas **ya normalizadas**, porque
    /// sobre las crudas " CTA-1" y "CTA-1" pasan como dos. Y el tope se comprueba antes de
    /// normalizar una por una: si llegan diez mil, rechazar en O(1) es mejor que recortar diez mil
    /// cadenas para terminar rechazando igual.
    /// </summary>
    private static List<CompanyBankAccount> NormalizeBankAccounts(
        IReadOnlyCollection<CompanyBankAccount> bankAccounts)
    {
        if (bankAccounts.Count == 0)
        {
            throw new CompaniesDomainException(
                "companies.company.bank_accounts_required",
                "The company needs at least one bank account.");
        }

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

    // Mismo criterio que Customer.EnsureValidCityId: acá sólo se comprueba que no venga vacía.
    // Que la fila exista es responsabilidad del handler, que resuelve contra
    // ICompanyGeographyLookup *antes* de llegar hasta acá — el dominio no puede llamar a
    // Geography para confirmarlo él mismo.
    private static Guid EnsureValidCityId(Guid cityId) =>
        cityId == Guid.Empty
            ? throw new CompaniesDomainException(
                "companies.company.city_required",
                "The city is required.")
            : cityId;

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
