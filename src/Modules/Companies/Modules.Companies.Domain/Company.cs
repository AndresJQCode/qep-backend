namespace Modules.Companies.Domain;

/// <summary>
/// Una empresa del tenant: la contraparte con la que se factura y se cotiza, identificada por su
/// numero de cuenta (RF-091). Guarda solo los datos maestros vivos; un documento congela su
/// propia copia de a quien se le emitio.
/// </summary>
public sealed class Company
{
    // Espejan los anchos de columna de companies.companies. Guardar aca significa que un valor
    // demasiado largo falla como 422 con codigo de dominio en vez de llegar a PostgreSQL y
    // volver como 500 server.unexpected. Los tres salen del schema del formulario que ya existe
    // en el frontend (features/companies/types/company-form.schema.ts).
    public const int NameMaxLength = 160;

    public const int AccountNumberMaxLength = 32;

    public const int TaxIdMaxLength = 32;

    // EF Core materializa por aca. El codigo nunca construye el agregado asi: Create es el unico
    // punto de entrada, y es el que hace cumplir los invariantes.
    private Company()
    {
        Name = string.Empty;
        AccountNumber = string.Empty;
        TaxId = string.Empty;
    }

    private Company(
        CompanyId id,
        Guid tenantId,
        string name,
        string accountNumber,
        string taxId,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        AccountNumber = accountNumber;
        TaxId = taxId;
        Apply(contact);
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public CompanyId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Unico por tenant; la unicidad vive en IX_companies_tenant_account_number.</summary>
    public string AccountNumber { get; private set; }

    /// <summary>
    /// NIT. **No es unico todavia.** El unico duplicado que el frontend rechaza hoy es el del
    /// numero de cuenta (companies.fixtures.ts), y un NIT repetido en dos empresas del mismo
    /// tenant puede ser un error de datos o una sucursal — nadie lo decidio. Si el gate del
    /// modulo lo cierra en "unico", es un segundo indice unico **con su propio codigo de
    /// dominio**, nunca una rama compartida con la del numero de cuenta.
    /// </summary>
    public string TaxId { get; private set; }

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
        string accountNumber,
        string taxId,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt) =>
        new(
            id,
            tenantId,
            NormalizeName(name),
            NormalizeAccountNumber(accountNumber),
            NormalizeTaxId(taxId),
            contact,
            occurredAt);

    public void Update(
        string name,
        string accountNumber,
        string taxId,
        CompanyContactInfo contact,
        DateTimeOffset occurredAt)
    {
        EnsureActive();

        Name = NormalizeName(name);
        AccountNumber = NormalizeAccountNumber(accountNumber);
        TaxId = NormalizeTaxId(taxId);
        Apply(contact);
        Version++;
        UpdatedAt = occurredAt;
    }

    // Asigna los tres siempre, incluidos los null. Se puede **limpiar** un campo, no solo
    // setearlo: una implementacion que ignore los null "para no pisar" deja campos imborrables y
    // pasa todas las demas pruebas. Por eso UpdateClearsTheOptionalFieldsThatArriveNull existe.
    private void Apply(CompanyContactInfo contact)
    {
        var normalized = contact.Normalized();

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
    // No revalida la unicidad del numero de cuenta a proposito:
    // IX_companies_tenant_account_number es unico **sin filtro parcial**, asi que desactivar
    // nunca libero el numero y reactivar no puede colisionar con nadie. Si alguien le agrega un
    // filtro parcial al indice, esta suposicion deja de valer.
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

    // Recortar espacios es parte del invariante, no higiene del llamador: el indice unico trata
    // " CTA-1" y "CTA-1" como dos numeros de cuenta distintos, cosa que nadie leyendo la lista
    // haria.
    //
    // No se pasa a mayusculas. Seria defendible para un numero de cuenta, pero nada en el
    // frontend ni en los requisitos dice que "cta-1" y "CTA-1" sean el mismo, y el precedente
    // del modulo vecino —Product.Code— tampoco lo hace. Si el gate decide lo contrario, el
    // cambio es aca y viene con su migracion.
    private static string NormalizeName(string name) =>
        Normalize(
            name,
            NameMaxLength,
            "companies.company.name_required",
            "The company name is required.",
            "companies.company.name_too_long",
            $"The company name cannot exceed {NameMaxLength} characters.");

    private static string NormalizeAccountNumber(string accountNumber) =>
        Normalize(
            accountNumber,
            AccountNumberMaxLength,
            "companies.company.account_number_required",
            "The company account number is required.",
            "companies.company.account_number_too_long",
            $"The company account number cannot exceed {AccountNumberMaxLength} characters.");

    private static string NormalizeTaxId(string taxId) =>
        Normalize(
            taxId,
            TaxIdMaxLength,
            "companies.company.tax_id_required",
            "The company tax id is required.",
            "companies.company.tax_id_too_long",
            $"The company tax id cannot exceed {TaxIdMaxLength} characters.");

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
