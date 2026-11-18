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

    public Task<bool> IdentificationExistsAsync(Guid tenantId, string identificationNumber, CancellationToken cancellationToken) =>
        Task.FromResult(Distributors.Any(item =>
            item.TenantId == tenantId && item.IdentificationNumber == identificationNumber.Trim()));

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

internal sealed class FakeEventPublisher : IWholesaleEventPublisher
{
    public List<PublishedInvitation> Invitations { get; } = [];

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

    public Task<Guid> RegisterOwnerTenantAsync(
        Guid ownerUserId, TenantRegistrationData data, string correlationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The invitation registers with the reserved id.");

    public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
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
    public List<(Guid WholesalerTenantId, Guid Actor, string BusinessName, string Identification, string Email)> Calls { get; } = [];

    public Guid CustomerId { get; } = Guid.CreateVersion7();

    /// <summary>Lanza en las próximas N llamadas (una falla a mitad de la aceptación).</summary>
    public int FailTimes { get; set; }

    public Task<Guid> EnsureCustomerAsync(
        Guid wholesalerTenantId,
        Guid actorUserId,
        string businessName,
        string identificationNumber,
        string email,
        CancellationToken cancellationToken)
    {
        if (FailTimes > 0)
        {
            FailTimes--;
            throw new InvalidOperationException("Customers is down.");
        }

        Calls.Add((wholesalerTenantId, actorUserId, businessName, identificationNumber, email));
        return Task.FromResult(CustomerId);
    }
}
