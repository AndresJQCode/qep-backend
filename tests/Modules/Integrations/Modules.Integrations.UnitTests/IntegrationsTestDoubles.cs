using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Integrations.UnitTests;

internal sealed class FakeExecutionContext(Guid tenantId, Guid subjectId, params string[] permissions)
    : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
}

internal sealed class FakeMembershipDirectory : IMembershipDirectory
{
    public Dictionary<(Guid UserId, Guid TenantId), Guid> Active { get; } = [];

    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>(null);

    public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Active.TryGetValue((userId, tenantId), out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(Active.Where(entry => entry.Key.UserId == userId).Select(entry => entry.Value).ToList());
}

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

/// <summary>Sin entrada = tenant que no está en <c>tenancy.tenants</c> (el stub): <c>null</c>, todo visible.</summary>
internal sealed class FakeTenantModules : ITenantModules
{
    public Dictionary<Guid, TenantModuleSet> Sets { get; } = [];

    public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<TenantModuleSet?>(Sets.TryGetValue(tenantId, out var set) ? set : null);
}

internal sealed class InMemoryConnectionRepository : IIntegrationConnectionRepository
{
    public List<IntegrationConnection> Connections { get; } = [];

    public Task<IntegrationConnection?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(Connections.SingleOrDefault(connection => connection.TenantId == tenantId && connection.Id == connectionId));

    public Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IntegrationConnection>>(Connections.Where(connection => connection.TenantId == tenantId).ToList());

    public Task<int> CountAsync(Guid tenantId, string providerKey, CancellationToken cancellationToken) =>
        Task.FromResult(Connections.Count(connection =>
            connection.TenantId == tenantId && string.Equals(connection.ProviderKey, providerKey, StringComparison.Ordinal)));

    public void Add(IntegrationConnection connection) => Connections.Add(connection);

    public void Remove(IntegrationConnection connection) => Connections.Remove(connection);
}

internal sealed class FakeUnitOfWork : IIntegrationsUnitOfWork
{
    public int Saves { get; private set; }

    public Exception? FailWith { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (FailWith is { } failure)
        {
            throw failure;
        }

        Saves++;
        return Task.FromResult(1);
    }
}

/// <summary>
/// Un "cifrado" legible que respeta el contrato de <see cref="ISecretProtector"/>: sólo abre lo que se
/// selló para la misma conexión y el mismo campo. <see cref="Unreadable"/> simula una llave retirada.
/// </summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string? ActiveKeyId { get; set; } = "test";

    public HashSet<(Guid ConnectionId, string FieldKey)> Unreadable { get; } = [];

    public bool HasKey(string keyId) => string.Equals(keyId, "test", StringComparison.Ordinal);

    public ProtectedSecret Protect(Guid connectionId, string fieldKey, string plaintext) =>
        ActiveKeyId is { } keyId
            ? new ProtectedSecret(keyId, Encoding.UTF8.GetBytes($"{connectionId:D}|{fieldKey}|{plaintext}"))
            : throw new InvalidOperationException("No active key.");

    public string Unprotect(Guid connectionId, string fieldKey, ProtectedSecret secret) =>
        TryUnprotect(connectionId, fieldKey, secret, out var plaintext)
            ? plaintext
            : throw new CryptographicException("Unreadable.");

    public bool TryUnprotect(
        Guid connectionId, string fieldKey, ProtectedSecret secret, [NotNullWhen(true)] out string? plaintext)
    {
        var prefix = $"{connectionId:D}|{fieldKey}|";
        var text = Encoding.UTF8.GetString(secret.Ciphertext);
        if (Unreadable.Contains((connectionId, fieldKey)) || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            plaintext = null;
            return false;
        }

        plaintext = text[prefix.Length..];
        return true;
    }
}

internal sealed class FakeAuthorNames : IConnectionAuthorNames
{
    public Dictionary<Guid, string> Names { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(
            Names.Where(entry => memberIds.Contains(entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value));
}

/// <summary>
/// Todo lo que un handler necesita, con valores por defecto: un tenant, un sujeto con membresía activa
/// y los dos permisos. Clase partial: las Tasks 7, 8 y 9 suman su parte en archivos propios.
/// </summary>
internal sealed partial class IntegrationsTestBed
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
    public const string Token = "zenvia-token-TEST-1";
    public const string FromNumber = "573001234567";
    public const string AuthorName = "Laura Gómez";

    public IntegrationsTestBed(IIntegrationProviderCatalog? catalog = null)
    {
        Catalog = catalog ?? new IntegrationProviderCatalog();
        Memberships.Active[(SubjectId, TenantId)] = MemberId;
        AuthorNames.Names[MemberId] = AuthorName;
    }

    public Guid TenantId { get; } = Guid.CreateVersion7();

    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public Guid MemberId { get; } = Guid.CreateVersion7();

    public IIntegrationProviderCatalog Catalog { get; }

    public FakeClock Clock { get; } = new(Now);

    public FakeTenantModules Modules { get; } = new();

    public FakeMembershipDirectory Memberships { get; } = new();

    public InMemoryConnectionRepository Repository { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public FakeSecretProtector Protector { get; } = new();

    public FakeAuthorNames AuthorNames { get; } = new();

    public string[] Permissions { get; set; } =
        [IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage];

    public FakeExecutionContext Context(Guid? tenantId = null) => new(tenantId ?? TenantId, SubjectId, Permissions);

    public IntegrationConnection Seed(string name = "WhatsApp sede norte", Guid? tenantId = null)
    {
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Zenvia,
            tenantId ?? TenantId,
            name,
            new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = FromNumber },
            new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = Token },
            Protector.Protect,
            MemberId,
            Now);
        Repository.Connections.Add(connection);
        return connection;
    }

    public void ShowEverything() =>
        Modules.Sets[TenantId] = TenantModuleSet.FromStored(TenantModuleKeys.All);

    public void HideQuotations() =>
        Modules.Sets[TenantId] = TenantModuleSet.FromStored(TenantModuleKeys.All.Except([TenantModuleKeys.Quotations]));

    public GetIntegrationsCatalogHandler CatalogHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Modules, context ?? Context());

    public ListConnectionsHandler ListHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Modules, Protector, AuthorNames, context ?? Context());

    public GetConnectionHandler GetHandler(IExecutionContext? context = null) =>
        new(Catalog, Repository, Modules, Protector, AuthorNames, context ?? Context());
}
