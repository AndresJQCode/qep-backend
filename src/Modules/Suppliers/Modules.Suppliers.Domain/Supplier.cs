namespace Modules.Suppliers.Domain;

/// <summary>
/// Un proveedor del tenant: la contraparte a la que se le compra. Directorio simple (SUP-01): sin
/// código emitido y sin libreta de direcciones. El documento (tipo + número) es **único por tenant**,
/// también entre inactivos (spec D-5): lo arbitra el índice <c>IX_suppliers_tenant_identification</c>,
/// sin filtro parcial, y el UnitOfWork de Infrastructure (SUP-05) lo traduce a
/// <c>suppliers.supplier.identification_taken</c>. Este agregado no lo comprueba: reactivar es el
/// camino, no duplicar.
///
/// Núcleo de identidad idéntico a <c>Customer</c> (longitudes, normalización, carril de ciudad por
/// país). Lo que cambia es lo opcional: teléfono, correo y dirección pueden faltar, y cuentas
/// bancarias y contactos pueden ser cero.
/// </summary>
public sealed class Supplier
{
    private readonly List<SupplierBankAccount> _bankAccounts = [];

    private readonly List<SupplierContact> _contacts = [];

    public const int NameMaxLength = 160;

    public const int BusinessNameMaxLength = 160;

    public const int CityNameMaxLength = 120;

    public const int CategoryMaxLength = 80;

    public const int NotesMaxLength = 2000;

    /// <summary>Plazo de pago en días; 0 es de contado.</summary>
    public const int PaymentTermMaxDays = 365;

    /// <summary>
    /// Colombia en ISO-3166-1 alpha-2. Con este país el domicilio se ubica con la ciudad DIVIPOLA
    /// (<see cref="CityId"/>); con cualquier otro, con la ciudad escrita a mano
    /// (<see cref="CityName"/>).
    /// </summary>
    public const string ColombiaCountryCode = "CO";

    // EF Core materializa por acá. El código nunca construye el agregado así: Create es el único
    // punto de entrada y el que hace cumplir los invariantes.
    private Supplier()
    {
        Name = string.Empty;
        Identification = new SupplierIdentification(IdentificationType.Nit, string.Empty);
        Contact = new SupplierContactInfo { Country = string.Empty };
    }

    public SupplierId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string? BusinessName { get; private set; }

    public SupplierIdentification Identification { get; private set; }

    public SupplierContactInfo Contact { get; private set; }

    /// <summary>
    /// La ciudad DIVIPOLA, FK blanda a <c>geography.cities</c>: Guid y no un id tipado de
    /// Geography, mismo criterio que en Customers. **Sólo para Colombia**; nula en el extranjero.
    /// </summary>
    public Guid? CityId { get; private set; }

    /// <summary>La ciudad escrita a mano, **sólo fuera de Colombia**; nula en Colombia.</summary>
    public string? CityName { get; private set; }

    /// <summary><see cref="Contact"/>.Phone en E.164 con «+», o null si no hay teléfono o no parsea.</summary>
    public string? PhoneE164 { get; private set; }

    public int PaymentTermDays { get; private set; }

    public string? Category { get; private set; }

    public string? Notes { get; private set; }

    public IReadOnlyList<SupplierBankAccount> BankAccounts => _bankAccounts;

    public IReadOnlyList<SupplierContact> Contacts => _contacts;

    public bool IsActive { get; private set; }

    /// <summary>Contador de concurrencia optimista; sube en cada escritura.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Supplier Create(
        Guid tenantId,
        SupplierWriteData data,
        ISupplierPhoneNormalizer phones,
        DateTimeOffset now)
    {
        var supplier = new Supplier
        {
            Id = SupplierId.New(),
            TenantId = tenantId,
            IsActive = true,
            Version = 1,
            CreatedAt = now
        };

        supplier.Apply(data, phones, now);
        return supplier;
    }

    public void Update(SupplierWriteData data, ISupplierPhoneNormalizer phones, DateTimeOffset now)
    {
        Apply(data, phones, now);
        Version++;
    }

    public void Deactivate(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.already_inactive",
                "The supplier is already inactive.");
        }

        IsActive = false;
        Version++;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        if (IsActive)
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.already_active",
                "The supplier is already active.");
        }

        IsActive = true;
        Version++;
        UpdatedAt = now;
    }

    // Todo se normaliza a locales **antes** de asignar nada. Asignar campo por campo mientras se
    // valida deja el agregado a medio escribir cuando el segundo falla: el 422 le dice al llamador
    // que no se guardó nada, pero la instancia en memoria ya tiene el nombre nuevo y esa es la que
    // EF persiste en el siguiente SaveChanges (el defecto que EMP-08 corrigió en Company).
    private void Apply(SupplierWriteData data, ISupplierPhoneNormalizer phones, DateTimeOffset now)
    {
        var name = NormalizeRequired(
            data.Name,
            NameMaxLength,
            "suppliers.supplier.name_required",
            "The supplier name is required.",
            "suppliers.supplier.name_too_long",
            $"The supplier name cannot exceed {NameMaxLength} characters.");
        var businessName = NormalizeOptional(
            data.BusinessName,
            BusinessNameMaxLength,
            "suppliers.supplier.business_name_too_long",
            $"The supplier business name cannot exceed {BusinessNameMaxLength} characters.");
        var identification = SupplierIdentification.Create(
            IdentificationTypeParser.Parse(data.IdentificationType),
            data.IdentificationNumber);
        var contact = new SupplierContactInfo
        {
            Phone = data.Phone,
            Email = data.Email,
            Address = data.Address,
            Country = data.Country!
        }.Normalized();
        var (cityId, cityName) = EnsureValidLocation(
            contact.Country,
            data.CityId,
            NormalizeOptional(
                data.CityName,
                CityNameMaxLength,
                "suppliers.supplier.city_name_too_long",
                $"The supplier city cannot exceed {CityNameMaxLength} characters."));
        var paymentTermDays = data.PaymentTermDays is >= 0 and <= PaymentTermMaxDays
            ? data.PaymentTermDays
            : throw new SuppliersDomainException(
                "suppliers.supplier.payment_term_invalid",
                $"The payment term must be between 0 and {PaymentTermMaxDays} days.");
        var category = NormalizeOptional(
            data.Category,
            CategoryMaxLength,
            "suppliers.supplier.category_too_long",
            $"The supplier category cannot exceed {CategoryMaxLength} characters.");
        var notes = NormalizeOptional(
            data.Notes,
            NotesMaxLength,
            "suppliers.supplier.notes_too_long",
            $"The supplier notes cannot exceed {NotesMaxLength} characters.");
        var bankAccounts = NormalizeBankAccounts(data.BankAccounts);
        var contacts = NormalizeContacts(data.Contacts);

        Name = name;
        BusinessName = businessName;
        Identification = identification;
        Contact = contact;
        CityId = cityId;
        CityName = cityName;
        // Sin teléfono no hay nada que normalizar; con él, un número que no parsea queda en null
        // sin impedir la escritura.
        PhoneE164 = contact.Phone is null ? null : phones.ToE164(contact.Phone, contact.Country);
        PaymentTermDays = paymentTermDays;
        Category = category;
        Notes = notes;
        _bankAccounts.Clear();
        _bankAccounts.AddRange(bankAccounts);
        _contacts.Clear();
        _contacts.AddRange(contacts);
        UpdatedAt = now;
    }

    /// <summary>
    /// Un proveedor colombiano se queda sin <c>CityName</c> y uno de afuera sin <c>CityId</c>:
    /// guardar los dos dejaría dos respuestas a «dónde está» sin nada que diga cuál gana, y la que
    /// sobra envejece sola. Para Colombia el chequeo es mínimo (no vacía); que la ciudad exista lo
    /// resuelve el handler contra <c>ISupplierGeographyLookup</c>, que el dominio no puede llamar.
    /// </summary>
    private static (Guid? CityId, string? CityName) EnsureValidLocation(
        string country,
        Guid? cityId,
        string? cityName)
    {
        if (country == ColombiaCountryCode)
        {
            return cityId is { } id && id != Guid.Empty
                ? (id, null)
                : throw new SuppliersDomainException(
                    "suppliers.supplier.city_required",
                    "The supplier city is required.");
        }

        return cityName is null
            ? throw new SuppliersDomainException(
                "suppliers.supplier.city_name_required",
                "The supplier city is required for a supplier outside Colombia.")
            : (null, cityName);
    }

    // El tope se comprueba antes de normalizar una por una (rechazar en O(1) antes que recortar
    // diez mil cadenas) y los duplicados se buscan sobre las ya normalizadas: sobre las crudas,
    // " CTA-1" y "CTA-1" pasan como dos.
    private static List<SupplierBankAccount> NormalizeBankAccounts(
        IReadOnlyList<SupplierBankAccount> bankAccounts)
    {
        if (bankAccounts.Count > SupplierBankAccount.MaxPerSupplier)
        {
            throw new SuppliersDomainException(
                "suppliers.supplier.bank_accounts_too_many",
                $"A supplier cannot have more than {SupplierBankAccount.MaxPerSupplier} bank accounts.");
        }

        var normalized = new List<SupplierBankAccount>(bankAccounts.Count);
        var seen = new HashSet<(string, string, string)>();

        foreach (var account in bankAccounts.Select(account => account.Normalized()))
        {
            if (!seen.Add(account.DeduplicationKey()))
            {
                throw new SuppliersDomainException(
                    "suppliers.supplier.bank_account_duplicated",
                    "The same bank account is listed more than once.");
            }

            normalized.Add(account);
        }

        return normalized;
    }

    private static List<SupplierContact> NormalizeContacts(IReadOnlyList<SupplierContact> contacts) =>
        contacts.Count > SupplierContact.MaxPerSupplier
            ? throw new SuppliersDomainException(
                "suppliers.supplier.contacts_too_many",
                $"A supplier cannot have more than {SupplierContact.MaxPerSupplier} contacts.")
            : [.. contacts.Select(contact => contact.Normalized())];

    private static string NormalizeRequired(
        string? value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage) =>
        NormalizeOptional(value, maxLength, tooLongCode, tooLongMessage)
            ?? throw new SuppliersDomainException(requiredCode, requiredMessage);

    // Vacío y ausente son lo mismo: se guarda null y no una cadena vacía.
    private static string? NormalizeOptional(
        string? value,
        int maxLength,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new SuppliersDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }
}
