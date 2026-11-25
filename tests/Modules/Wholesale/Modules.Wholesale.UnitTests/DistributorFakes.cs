using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Wholesale.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.UnitTests;

internal sealed class FakeDistributorRepository : IDistributorRepository
{
    public List<Distributor> Distributors { get; } = [];

    public List<Distributor> Removed { get; } = [];

    public int Writes { get; private set; }

    public Distributor Seed(Distributor distributor)
    {
        Distributors.Add(distributor);
        return distributor;
    }

    /// <summary>Lo que vería una lectura nueva después de soltar los cambios: la fila «de la base».</summary>
    public void Replace(Distributor stale, Distributor fresh)
    {
        Distributors.Remove(stale);
        Distributors.Add(fresh);
    }

    public Task<Distributor?> GetAsync(Guid tenantId, DistributorId id, CancellationToken cancellationToken) =>
        Task.FromResult(Distributors.FirstOrDefault(item => item.TenantId == tenantId && item.Id == id));

    public Task<Distributor?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(Distributors.FirstOrDefault(item => item.Invitation.TokenHash == tokenHash));

    public Task<bool> IdentificationExistsAsync(
        Guid tenantId,
        string identificationType,
        string identificationNumber,
        DistributorId? excluding,
        CancellationToken cancellationToken) =>
        Task.FromResult(Distributors.Any(item =>
            item.TenantId == tenantId
            && item.Id != excluding
            && item.IdentificationType == identificationType
            && item.IdentificationNumber == identificationNumber.Trim()));

    public Task<Distributor?> FindByTenantsAsync(
        Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Distributors.FirstOrDefault(item =>
            item.TenantId == wholesalerTenantId && item.DistributorTenantId == distributorTenantId));

    public Task<bool> IsLinkedAsync(Guid wholesalerTenantId, Guid distributorTenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Distributors.Any(item =>
            item.TenantId == wholesalerTenantId && item.DistributorTenantId == distributorTenantId));

    public Task<IReadOnlySet<Guid>> LinkedTenantIdsAsync(Guid wholesalerTenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(Distributors
            .Where(item => item.TenantId == wholesalerTenantId && item.DistributorTenantId.HasValue)
            .Select(item => item.DistributorTenantId!.Value)
            .ToHashSet());

    public Task<(IReadOnlyList<Distributor> Items, int Total)> SearchAsync(
        Guid tenantId, DistributorStatus? status, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matching = Matching(tenantId, search).Where(item => status is null || item.Status == status).ToList();
        IReadOnlyList<Distributor> items = matching
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id.Value)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return Task.FromResult((items, matching.Count));
    }

    public Task<IReadOnlyDictionary<DistributorStatus, int>> CountByStatusAsync(
        Guid tenantId, string? search, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<DistributorStatus, int>>(Matching(tenantId, search)
            .GroupBy(item => item.Status)
            .ToDictionary(group => group.Key, group => group.Count()));

    public void Add(Distributor distributor)
    {
        Writes++;
        Distributors.Add(distributor);
    }

    public void Remove(Distributor distributor)
    {
        Writes++;
        Distributors.Remove(distributor);
        Removed.Add(distributor);
    }

    private IEnumerable<Distributor> Matching(Guid tenantId, string? search) =>
        Distributors.Where(item => item.TenantId == tenantId
            && (search is null
                || item.BusinessName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.IdentificationNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Email.Contains(search, StringComparison.OrdinalIgnoreCase)));
}

internal sealed record PublishedInvitation(
    Guid TenantId,
    Guid DistributorId,
    string Email,
    string BusinessName,
    string Token,
    string EmailProof,
    DateTimeOffset ExpiresAt,
    string CorrelationId,
    DateTimeOffset OccurredAt);

internal sealed record PublishedNotice(
    Guid TenantId,
    Guid DistributorId,
    string Phone,
    Guid ConnectionId,
    Guid SentByMemberId,
    string ContactName,
    string Email,
    string Token,
    int Generation,
    string CorrelationId,
    DateTimeOffset OccurredAt);

internal sealed record PublishedEmailChange(
    Guid TenantId,
    Guid DistributorId,
    string PreviousEmail,
    string NewEmail,
    string BusinessName,
    string CorrelationId,
    DateTimeOffset OccurredAt);

internal sealed class FakeEventPublisher : IWholesaleEventPublisher
{
    public List<PublishedInvitation> Invitations { get; } = [];

    public List<PublishedNotice> Notices { get; } = [];

    public List<PublishedEmailChange> EmailChanges { get; } = [];

    public void DistributorEmailChanged(
        Guid tenantId,
        Guid distributorId,
        string previousEmail,
        string newEmail,
        string businessName,
        string correlationId,
        DateTimeOffset now) =>
        EmailChanges.Add(new PublishedEmailChange(
            tenantId, distributorId, previousEmail, newEmail, businessName, correlationId, now));

    /// <summary>El aviso con link (spec 2026-11-19 §2); el viejo sin link ya no tiene método.</summary>
    public void InvitationLinkRequested(
        Guid tenantId,
        Guid distributorId,
        string phone,
        Guid connectionId,
        Guid sentByMemberId,
        string contactName,
        string email,
        string token,
        int generation,
        string correlationId,
        DateTimeOffset occurredAt) =>
        Notices.Add(new PublishedNotice(
            tenantId, distributorId, phone, connectionId, sentByMemberId, contactName, email, token, generation, correlationId,
            occurredAt));

    public void DistributorInvited(
        Guid tenantId,
        Guid distributorId,
        string email,
        string businessName,
        string token,
        string emailProof,
        DateTimeOffset expiresAt,
        string correlationId,
        DateTimeOffset occurredAt) =>
        Invitations.Add(new PublishedInvitation(
            tenantId, distributorId, email, businessName, token, emailProof, expiresAt, correlationId, occurredAt));
}

/// <summary>El link de la invitación con una base fija; <see cref="TokenOf"/> saca el token de vuelta.</summary>
internal sealed class FakeInvitationLinks : IWholesaleInvitationLinks
{
    private const string Base = "https://app.test/wholesale-invitations/";

    public static string Compose(string token) => Base + Uri.EscapeDataString(token);

    public static string TokenOf(string? url)
    {
        Assert.NotNull(url);
        Assert.StartsWith(Base, url, StringComparison.Ordinal);
        return Uri.UnescapeDataString(url[Base.Length..]);
    }

    string IWholesaleInvitationLinks.Compose(string token) => Compose(token);
}

internal sealed class FakeCompanyLookup : IWholesaleCompanyLookup
{
    public List<WholesaleCompanyRef> Companies { get; } = [];

    public WholesaleCompanyRef Add(string name, string documentType = "NIT", bool isActive = true)
    {
        var company = new WholesaleCompanyRef(
            Guid.CreateVersion7(), name, documentType, "900111222", "CO", Guid.CreateVersion7(), "Medellín",
            "3001234567", "ventas@mayorista.co", "Calle 1 # 2-3", isActive,
            documentType is "NIT" or "CC" or "CE" or "PASAPORTE");
        Companies.Add(company);
        return company;
    }

    public Task<WholesaleCompanyRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
        Task.FromResult(Companies.FirstOrDefault(company => company.Id == companyId));

    public Task<IReadOnlyList<WholesaleCompanyRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WholesaleCompanyRef>>(Companies.Where(company => company.IsActive).ToList());
}

/// <summary>Geografía en memoria: sólo las ciudades que se siembran existen; <c>Calls</c> anota cada búsqueda.</summary>
internal sealed class FakeCityLookup : IWholesaleCityLookup
{
    public Dictionary<Guid, string> Cities { get; } = [];

    public List<Guid> Calls { get; } = [];

    /// <summary>Todas las ciudades sembradas quedan en este departamento: ninguna prueba distingue dos.</summary>
    public Guid DepartmentId { get; } = Guid.CreateVersion7();

    public Guid Add(string name)
    {
        var id = Guid.CreateVersion7();
        Cities[id] = name;
        return id;
    }

    public Task<WholesaleCityRef?> FindAsync(Guid cityId, CancellationToken cancellationToken)
    {
        Calls.Add(cityId);
        return Task.FromResult(Cities.TryGetValue(cityId, out var name)
            ? new WholesaleCityRef(cityId, name, DepartmentId, "Antioquia")
            : null);
    }
}

internal sealed class FakeMemberDirectory : IWholesaleMemberDirectory
{
    public List<WholesaleMember> Active { get; } = [];

    /// <summary>Nombres de cualquier membresía, activa o no.</summary>
    public Dictionary<Guid, string> Names { get; } = [];

    public int FindNamesCalls { get; private set; }

    public Guid AddActive(string name)
    {
        var id = Guid.CreateVersion7();
        Active.Add(new WholesaleMember(id, name));
        Names[id] = name;
        return id;
    }

    public Task<IReadOnlyList<WholesaleMember>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WholesaleMember>>(Active.ToList());

    public Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        FindNamesCalls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(memberIds
            .Distinct()
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]));
    }
}

internal sealed class FakeTenantNames : IWholesaleTenantDirectory
{
    public Dictionary<Guid, string> Names { get; } = [];

    public int Calls { get; private set; }

    public Task<IReadOnlyDictionary<Guid, string>> FindNamesAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(tenantIds
            .Distinct()
            .Where(Names.ContainsKey)
            .ToDictionary(id => id, id => Names[id]));
    }
}

/// <summary>Tenancy en memoria para la aceptación: tenants con estado, nombre, módulos y owner.</summary>
internal sealed class FakeTenancy : ITenantDirectory, ITenantModules, ITenantRegistration, ITenantModuleGrants, ITenantOwnership
{
    public Dictionary<Guid, (string Name, TenantStatus Status, Guid Owner, HashSet<TenantModuleKey> Modules)> Tenants { get; } = [];

    /// <summary>Miembros activos con el rol de sistema admin, por tenant (además del owner).</summary>
    public Dictionary<Guid, HashSet<Guid>> Admins { get; } = [];

    public List<(Guid TenantId, Guid Owner, TenantRegistrationData Data, IReadOnlyList<TenantModuleKey> Modules)> Registered { get; } = [];

    public List<(Guid TenantId, IReadOnlyList<TenantModuleKey> Keys, Guid Actor, string Note)> Grants { get; } = [];

    public List<TenantRegistrationData> Checked { get; } = [];

    /// <summary>Corre justo antes de cada escritura de Tenancy: mira qué estaba confirmado en ese momento.</summary>
    public Action? BeforeWrite { get; set; }

    /// <summary>Cualquier escritura de Tenancy: «no escribió nada» es cero.</summary>
    public int Writes => Registered.Count + Grants.Count;

    public Guid AddTenant(string name, Guid owner, TenantStatus status = TenantStatus.Active, params TenantModuleKey[] modules) =>
        AddTenant(Guid.CreateVersion7(), name, owner, status, modules);

    public Guid AddTenant(Guid id, string name, Guid owner, TenantStatus status, params TenantModuleKey[] modules)
    {
        Tenants[id] = (name, status, owner, modules.ToHashSet());
        return id;
    }

    public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.TryGetValue(tenantId.Value, out var tenant) ? tenant.Name : null);

    /// <summary>Los tenants por cuyo estado se preguntó.</summary>
    public List<Guid> StatusQueries { get; } = [];

    public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        StatusQueries.Add(tenantId.Value);
        return Task.FromResult(Tenants.TryGetValue(tenantId.Value, out var tenant) ? (TenantStatus?)tenant.Status : null);
    }

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.TryGetValue(tenantId, out var tenant) ? TenantModuleSet.FromStored(tenant.Modules) : null);

    /// <summary>Corre antes del chequeo del slug: simula otra aceptación que commiteó el tenant justo antes.</summary>
    public Action? BeforeEnsureRegistrable { get; set; }

    public Task EnsureRegistrableAsync(TenantRegistrationData data, CancellationToken cancellationToken)
    {
        BeforeEnsureRegistrable?.Invoke();
        Checked.Add(data);
        if (Tenants.Values.Any(tenant => tenant.Name == data.DisplayName))
        {
            throw new TenantDomainException("tenancy.slug.taken", "taken");
        }

        return Task.CompletedTask;
    }

    public Task<bool> RegisterInvitedTenantAsync(
        TenantId tenantId,
        Guid ownerUserId,
        TenantRegistrationData data,
        IReadOnlyList<TenantModuleKey> modules,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (Tenants.TryGetValue(tenantId.Value, out var existing))
        {
            return existing.Owner == ownerUserId
                ? Task.FromResult(false)
                : throw new InvalidOperationException("Another owner.");
        }

        BeforeWrite?.Invoke();
        Registered.Add((tenantId.Value, ownerUserId, data, modules));
        Tenants[tenantId.Value] = (data.DisplayName, TenantStatus.Active, ownerUserId, modules.ToHashSet());
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TenantModuleKey>> EnsureActiveAsync(
        TenantId tenantId, IReadOnlyList<TenantModuleKey> keys, Guid actorUserId, string note, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();
        Grants.Add((tenantId.Value, keys, actorUserId, note));
        var tenant = Tenants[tenantId.Value];
        IReadOnlyList<TenantModuleKey> enabled = TenantModuleKeys.All
            .Where(key => keys.Contains(key) && !tenant.Modules.Contains(key))
            .ToList();
        tenant.Modules.UnionWith(enabled);
        return Task.FromResult(enabled);
    }

    public Task<IReadOnlyList<OwnedTenant>> ListOwnedActiveTenantsAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OwnedTenant>>(Tenants
            .Where(entry => entry.Value.Owner == userId && entry.Value.Status == TenantStatus.Active)
            .OrderBy(entry => entry.Value.Name, StringComparer.Ordinal)
            .Select(entry => new OwnedTenant(entry.Key, entry.Value.Name))
            .ToList());

    public Task<IReadOnlyList<OwnedTenant>> ListAdministeredActiveTenantsAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OwnedTenant>>(Tenants
            .Where(entry => entry.Value.Status == TenantStatus.Active
                && (entry.Value.Owner == userId
                    || (Admins.TryGetValue(entry.Key, out var admins) && admins.Contains(userId))))
            .OrderBy(entry => entry.Value.Name, StringComparer.Ordinal)
            .Select(entry => new OwnedTenant(entry.Key, entry.Value.Name))
            .ToList());

    public Task<Guid> RegisterOwnerTenantAsync(
        Guid ownerUserId, TenantRegistrationData data, string correlationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The invitation registers with the reserved id.");

    public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>El país del tenant (la fila de configuración regional); sin entrada, el tenant no tiene fila.</summary>
    public Dictionary<Guid, string> Countries { get; } = [];

    public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Countries.TryGetValue(tenantId.Value, out var country)
            ? new TenantRegionalSettings("America/Bogota", "dd/MM/yyyy", "COP", "es-CO", country)
            : null);
}

/// <summary>Un normalizador de mentira: sólo dígitos, espacios y un «+» inicial; antepone el indicativo del país (57 o 52)
/// salvo que ya venga con «+». Anota el país con que lo llamaron.</summary>
internal sealed class FakePhoneNormalizer : IWholesalePhoneNormalizer
{
    public List<(string Phone, string Country)> Calls { get; } = [];

    public string? ToE164(string phone, string country)
    {
        Calls.Add((phone, country));
        if (phone.Any(character => !char.IsAsciiDigit(character) && character is not (' ' or '+')))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (phone.StartsWith('+'))
        {
            return "+" + digits;
        }

        return (country == "MX" ? "+52" : "+57") + digits;
    }
}

/// <summary>
/// Las conexiones listas para el aviso. Con <see cref="ThrowWhenListed"/> falla resolver cuáles están listas: la prueba de que
/// un <c>PUT</c> sin cambio no resuelve nada (D-9) y de que un reenvío sigue si Integrations falla. Con
/// <see cref="ThrowWhenLabeled"/> falla pedir el label, que el detalle sólo pide si hay aviso (D-7). <see cref="ThrowWhenCalled"/>
/// prende las dos.
/// </summary>
internal sealed class FakeWhatsAppConnections : IWholesaleWhatsAppConnections
{
    public List<WholesaleWhatsAppConnection> Ready { get; } = [];

    /// <summary>Labels de conexiones activas, listas o no.</summary>
    public Dictionary<Guid, string> Labels { get; } = [];

    public bool ThrowWhenListed { get; set; }

    public bool ThrowWhenLabeled { get; set; }

    public bool ThrowWhenCalled
    {
        get => ThrowWhenListed && ThrowWhenLabeled;
        set => (ThrowWhenListed, ThrowWhenLabeled) = (value, value);
    }

    public int ListCalls { get; private set; }

    public Guid AddReady(string label)
    {
        var id = Guid.CreateVersion7();
        Ready.Add(new WholesaleWhatsAppConnection(id, label));
        Labels[id] = label;
        return id;
    }

    public Task<IReadOnlyList<WholesaleWhatsAppConnection>> ListReadyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        ListCalls++;
        return ThrowWhenListed
            ? throw new InvalidOperationException("The connections should not be resolved.")
            : Task.FromResult<IReadOnlyList<WholesaleWhatsAppConnection>>(Ready.ToList());
    }

    public Task<string?> FindLabelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        ThrowWhenLabeled
            ? throw new InvalidOperationException("The connection label should not be read.")
            : Task.FromResult(Labels.GetValueOrDefault(connectionId));
}

/// <summary>La membresía activa de cada usuario en cada tenant (D-6).</summary>
internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Active { get; } = [];

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Active.TryGetValue((userId, tenantId), out var member) ? (Guid?)member : null);

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class FakeSupplierLinker : IWholesaleSupplierLinker
{
    public List<(Guid DistributorTenantId, Guid WholesalerTenantId, string WholesalerName, WholesaleCompanyRef Company, Guid Actor)> Calls { get; } = [];

    public Exception? Fail { get; set; }

    public Task<Guid> EnsureLinkedSupplierAsync(
        Guid distributorTenantId,
        Guid wholesalerTenantId,
        string wholesalerName,
        WholesaleCompanyRef company,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        if (Fail is { } failure)
        {
            throw failure;
        }

        Calls.Add((distributorTenantId, wholesalerTenantId, wholesalerName, company, actorUserId));
        return Task.FromResult(Guid.CreateVersion7());
    }
}

internal sealed class FakeCompanyProvisioning : IWholesaleCompanyProvisioning
{
    public List<(Guid Tenant, Guid Actor, WholesaleCompanyDraft Draft)> Calls { get; } = [];

    public Guid CompanyId { get; } = Guid.CreateVersion7();

    /// <summary>Lo que Companies rechazaría (un <c>ValidationException</c>, por ejemplo).</summary>
    public Exception? Fail { get; set; }

    /// <summary>Corre justo antes de cada escritura de Companies.</summary>
    public Action? BeforeWrite { get; set; }

    /// <summary>Las llamadas de «sólo si el tenant no tiene ninguna»; la decisión de saltarla es de Companies, no de Wholesale.</summary>
    public List<(Guid Tenant, Guid Actor, WholesaleCompanyDraft Draft)> FirstCalls { get; } = [];

    public Task<Guid?> EnsureFirstCompanyAsync(
        Guid distributorTenantId,
        Guid actorUserId,
        WholesaleCompanyDraft draft,
        CancellationToken cancellationToken)
    {
        if (Fail is { } failure)
        {
            throw failure;
        }

        BeforeWrite?.Invoke();
        FirstCalls.Add((distributorTenantId, actorUserId, draft));
        return Task.FromResult<Guid?>(CompanyId);
    }

    public Task<Guid> EnsureCompanyAsync(
        Guid distributorTenantId,
        Guid actorUserId,
        WholesaleCompanyDraft draft,
        CancellationToken cancellationToken)
    {
        if (Fail is { } failure)
        {
            throw failure;
        }

        BeforeWrite?.Invoke();
        Calls.Add((distributorTenantId, actorUserId, draft));
        return Task.FromResult(CompanyId);
    }
}

internal sealed class FakeCustomerProvisioning : IWholesaleCustomerProvisioning
{
    public List<(Guid WholesalerTenantId, Guid Actor, string BusinessName, string Country, string IdentificationType, string Identification, string Email, string? Phone, Guid? CityId, string? CityName, string? Address, string ContactName)> Calls { get; } = [];

    public Guid CustomerId { get; } = Guid.CreateVersion7();

    /// <summary>Lanza en las próximas N llamadas (una falla a mitad de la aceptación).</summary>
    public int FailTimes { get; set; }

    public Task<Guid> EnsureCustomerAsync(
        Guid wholesalerTenantId,
        Guid actorUserId,
        string businessName,
        string country,
        string identificationType,
        string identificationNumber,
        string email,
        string? phone,
        Guid? cityId,
        string? cityName,
        string? address,
        string contactName,
        CancellationToken cancellationToken)
    {
        if (FailTimes > 0)
        {
            FailTimes--;
            throw new InvalidOperationException("Customers is down.");
        }

        Calls.Add((wholesalerTenantId, actorUserId, businessName, country, identificationType, identificationNumber, email, phone, cityId, cityName, address, contactName));
        return Task.FromResult(CustomerId);
    }
}
