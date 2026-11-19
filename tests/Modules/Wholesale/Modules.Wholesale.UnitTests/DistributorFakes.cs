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
        Guid tenantId, string identificationType, string identificationNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Distributors.Any(item =>
            item.TenantId == tenantId
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

    public Task<IReadOnlyDictionary<PriceListId, int>> CountByPriceListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<PriceListId, int>>(Distributors
            .Where(item => item.TenantId == tenantId)
            .GroupBy(item => item.PriceListId)
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
    int Generation,
    string CorrelationId,
    DateTimeOffset OccurredAt);

internal sealed class FakeEventPublisher : IWholesaleEventPublisher
{
    public List<PublishedInvitation> Invitations { get; } = [];

    public List<PublishedNotice> Notices { get; } = [];

    public void InvitationNoticeRequested(
        Guid tenantId,
        Guid distributorId,
        string phone,
        Guid connectionId,
        Guid sentByMemberId,
        string contactName,
        string email,
        int generation,
        string correlationId,
        DateTimeOffset occurredAt) =>
        Notices.Add(new PublishedNotice(
            tenantId, distributorId, phone, connectionId, sentByMemberId, contactName, email, generation, correlationId, occurredAt));

    public void DistributorInvited(
        Guid tenantId,
        Guid distributorId,
        string email,
        string businessName,
        string token,
        DateTimeOffset expiresAt,
        string correlationId,
        DateTimeOffset occurredAt) =>
        Invitations.Add(new PublishedInvitation(
            tenantId, distributorId, email, businessName, token, expiresAt, correlationId, occurredAt));
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

    public Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.TryGetValue(tenantId.Value, out var tenant) ? (TenantStatus?)tenant.Status : null);

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

        Registered.Add((tenantId.Value, ownerUserId, data, modules));
        Tenants[tenantId.Value] = (data.DisplayName, TenantStatus.Active, ownerUserId, modules.ToHashSet());
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TenantModuleKey>> EnsureActiveAsync(
        TenantId tenantId, IReadOnlyList<TenantModuleKey> keys, Guid actorUserId, string note, CancellationToken cancellationToken)
    {
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

/// <summary>Las conexiones listas para el aviso. Con <see cref="ThrowWhenCalled"/>, cualquier consulta falla: la prueba de
/// que un <c>PUT</c> sin cambio no resuelve nada (D-9).</summary>
internal sealed class FakeWhatsAppConnections : IWholesaleWhatsAppConnections
{
    public List<WholesaleWhatsAppConnection> Ready { get; } = [];

    /// <summary>Labels de conexiones activas, listas o no.</summary>
    public Dictionary<Guid, string> Labels { get; } = [];

    public bool ThrowWhenCalled { get; set; }

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
        return ThrowWhenCalled
            ? throw new InvalidOperationException("The connections should not be resolved.")
            : Task.FromResult<IReadOnlyList<WholesaleWhatsAppConnection>>(Ready.ToList());
    }

    public Task<string?> FindLabelAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        ThrowWhenCalled
            ? throw new InvalidOperationException("The connections should not be resolved.")
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

internal sealed class FakeCustomerProvisioning : IWholesaleCustomerProvisioning
{
    public List<(Guid WholesalerTenantId, Guid Actor, string BusinessName, string Country, string IdentificationType, string Identification, string Email)> Calls { get; } = [];

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
        CancellationToken cancellationToken)
    {
        if (FailTimes > 0)
        {
            FailTimes--;
            throw new InvalidOperationException("Customers is down.");
        }

        Calls.Add((wholesalerTenantId, actorUserId, businessName, country, identificationType, identificationNumber, email));
        return Task.FromResult(CustomerId);
    }
}
