namespace Modules.Customers.Domain;

/// <summary>Spec 2026-10-24 B2: los campos del que queda que la fusión llena. Viajan por nombre en <c>filledFields</c>.</summary>
public enum CustomerMergeField
{
    Email,
    BusinessName,
    Phone,
    WhatsAppUserId,
    InstagramUserId,
    Identification,
    Country,
    Address,
    MarketingOptOut,
}

/// <summary>
/// Spec 2026-10-24 §3: qué le pasa a cada ficha cuando se fusionan clientes. <see cref="Create"/> valida y calcula sin
/// escribir (la vista previa lo usa igual que la fusión); <see cref="MarkMerged"/> y <see cref="FillSurvivor"/> son los dos
/// pasos de escritura, que el repositorio guarda en <c>SaveChanges</c> distintos porque EF ordena los <c>UPDATE</c> por
/// clave primaria y los índices únicos del BSUID, el IGSID y el documento no admiten dos dueños ni un instante.
/// </summary>
public sealed class CustomerMergePlan
{
    private readonly Customer? _emailFrom;
    private readonly Customer? _businessNameFrom;
    private readonly Customer? _phoneFrom;
    private readonly Customer? _whatsAppFrom;
    private readonly Customer? _instagramFrom;
    private readonly Customer? _identificationFrom;
    private readonly Customer? _countryFrom;
    private readonly Customer? _addressFrom;
    private readonly Customer? _optOutFrom;

    // MarkMerged les quita BSUID, IGSID y documento a los fusionados antes de FillSurvivor: lo que el que queda va a
    // recibir se captura al planear.
    private readonly string? _whatsAppUserId;
    private readonly string? _instagramUserId;
    private readonly string? _instagramUsername;
    private readonly CustomerIdentification? _identification;
    // El tipo de persona viaja con el documento: se captura aquí por lo mismo que _identification (spec 2026-10-26).
    private readonly PersonType? _personType;
    private readonly MarketingConsentSource? _optOutSource;
    private readonly DateTimeOffset? _optOutAt;

    private CustomerMergePlan(Customer survivor, IReadOnlyList<Customer> merged)
    {
        Survivor = survivor;
        Merged = merged;

        // Para cada campo vacío del que queda, el primer fusionado (en el orden pedido) que lo tenga. D-F14: nunca la
        // clasificación ni el CUC.
        _emailFrom = survivor.Email is null ? merged.FirstOrDefault(c => c.Email is not null) : null;
        _businessNameFrom = survivor.BusinessName is null ? merged.FirstOrDefault(c => c.BusinessName is not null) : null;
        _phoneFrom = survivor.Phone is null ? merged.FirstOrDefault(c => c.Phone is not null) : null;
        _whatsAppFrom = survivor.WhatsAppUserId is null ? merged.FirstOrDefault(c => c.WhatsAppUserId is not null) : null;
        _instagramFrom = survivor.InstagramUserId is null ? merged.FirstOrDefault(c => c.InstagramUserId is not null) : null;
        if (!survivor.IsComplete)
        {
            _identificationFrom = survivor.Identification is null ? merged.FirstOrDefault(c => c.Identification is not null) : null;
            _countryFrom = survivor.Country is null ? merged.FirstOrDefault(c => c.Country is not null) : null;
            _addressFrom = survivor.Address is null ? merged.FirstOrDefault(c => c.Address is not null) : null;
        }

        _optOutFrom = survivor.MarketingOptOut ? null : merged.FirstOrDefault(c => c.MarketingOptOut);

        _whatsAppUserId = _whatsAppFrom?.WhatsAppUserId;
        _instagramUserId = _instagramFrom?.InstagramUserId;
        _instagramUsername = _instagramFrom?.InstagramUsername;
        _identification = _identificationFrom?.Identification;
        _personType = _identificationFrom?.PersonType;
        _optOutSource = _optOutFrom?.MarketingOptOutSource;
        _optOutAt = _optOutFrom?.MarketingOptOutAt;

        var filled = new List<CustomerMergeField>();
        AddIf(filled, _emailFrom, CustomerMergeField.Email);
        AddIf(filled, _businessNameFrom, CustomerMergeField.BusinessName);
        AddIf(filled, _phoneFrom, CustomerMergeField.Phone);
        AddIf(filled, _whatsAppFrom, CustomerMergeField.WhatsAppUserId);
        AddIf(filled, _instagramFrom, CustomerMergeField.InstagramUserId);
        AddIf(filled, _identificationFrom, CustomerMergeField.Identification);
        AddIf(filled, _countryFrom, CustomerMergeField.Country);
        AddIf(filled, _addressFrom, CustomerMergeField.Address);
        AddIf(filled, _optOutFrom, CustomerMergeField.MarketingOptOut);
        FilledFields = filled;
    }

    public Customer Survivor { get; }

    /// <summary>Los fusionados, en el orden pedido.</summary>
    public IReadOnlyList<Customer> Merged { get; }

    /// <summary>Lo que la fusión llena del que queda, en el orden del enum.</summary>
    public IReadOnlyList<CustomerMergeField> FilledFields { get; }

    /// <summary>
    /// Valida y calcula sin escribir. El primero que falla gana: un id repetido, el del que queda entre los fusionados o
    /// un tenant distinto son <see cref="ArgumentException"/> (el validador y el repositorio ya lo impiden); después,
    /// <c>already_merged</c>, <c>survivor_inactive</c> y <c>survivor_incomplete</c>. Un fusionado inactivo vale (D-F12).
    /// </summary>
    public static CustomerMergePlan Create(Customer survivor, IReadOnlyList<Customer> merged)
    {
        ArgumentNullException.ThrowIfNull(survivor);
        ArgumentNullException.ThrowIfNull(merged);
        if (merged.Count == 0)
        {
            throw new ArgumentException("At least one customer to merge is required.", nameof(merged));
        }

        var ids = new HashSet<CustomerId> { survivor.Id };
        foreach (var customer in merged)
        {
            if (!ids.Add(customer.Id))
            {
                throw new ArgumentException("A customer cannot appear twice or be merged into itself.", nameof(merged));
            }

            if (customer.TenantId != survivor.TenantId)
            {
                throw new ArgumentException("Every customer must belong to the same tenant.", nameof(merged));
            }
        }

        if (survivor.IsMerged || merged.Any(customer => customer.IsMerged))
        {
            throw new CustomersDomainException("customers.merge.already_merged", "A merged customer cannot take part in another merge.");
        }

        if (!survivor.IsActive)
        {
            throw new CustomersDomainException("customers.merge.survivor_inactive", "The customer that remains must be active.");
        }

        if (!survivor.IsComplete && merged.Any(customer => customer.IsComplete))
        {
            throw new CustomersDomainException(
                "customers.merge.survivor_incomplete", "An incomplete customer cannot remain when a complete one is merged into it.");
        }

        return new CustomerMergePlan(survivor, merged.ToArray());
    }

    /// <summary>Primer paso: marca cada fusionado y le quita lo que va a dar (BSUID, IGSID con su usuario, documento).</summary>
    public void MarkMerged(DateTimeOffset now)
    {
        foreach (var customer in Merged)
        {
            // El documento sólo sale de un incompleto (desvío F1): una ficha completa lo exige (CK_customers_complete_fields)
            // y conserva el suyo; lo libera el índice parcial.
            customer.ApplyMergedInto(
                Survivor.Id,
                now,
                releaseWhatsAppUserId: ReferenceEquals(customer, _whatsAppFrom),
                releaseInstagram: ReferenceEquals(customer, _instagramFrom),
                releaseIdentification: ReferenceEquals(customer, _identificationFrom));
        }
    }

    /// <summary>Segundo paso: llena el que queda, hereda el opt-out y siempre sube su versión (Review Focus 5).</summary>
    public void FillSurvivor(DateTimeOffset now)
    {
        if (_emailFrom is not null)
        {
            Survivor.FillEmail(_emailFrom.Email!);
        }

        if (_businessNameFrom is not null)
        {
            Survivor.FillBusinessName(_businessNameFrom.BusinessName!);
        }

        if (_phoneFrom is not null)
        {
            Survivor.FillPhone(_phoneFrom.Phone!, _phoneFrom.PhoneE164);
        }

        if (_whatsAppUserId is not null)
        {
            Survivor.FillWhatsAppUserId(_whatsAppUserId);
        }

        if (_instagramUserId is not null)
        {
            Survivor.FillInstagram(_instagramUserId, _instagramUsername);
        }

        if (_identification is { } identification)
        {
            Survivor.FillIdentification(identification.Type, identification.Number, _personType);
        }

        if (_countryFrom is not null)
        {
            Survivor.FillCountry(_countryFrom.Country!);
        }

        if (_addressFrom is not null)
        {
            Survivor.FillAddress(_addressFrom.Address!);
        }

        if (_optOutFrom is not null)
        {
            Survivor.OptOutOfMarketing(_optOutSource ?? MarketingConsentSource.Manual, _optOutAt ?? now);
        }

        Survivor.TouchForMerge(now);
    }

    private static void AddIf(List<CustomerMergeField> filled, Customer? from, CustomerMergeField field)
    {
        if (from is not null)
        {
            filled.Add(field);
        }
    }
}
