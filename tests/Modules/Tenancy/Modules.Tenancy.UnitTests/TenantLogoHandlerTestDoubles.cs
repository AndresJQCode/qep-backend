using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Modules.Audit.Application;
using Modules.Audit.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

// Dobles de los puertos que usan SetTenantLogoHandler y RemoveTenantLogoHandler. A mano y sin
// librería de mocking, como el resto del repositorio. Storage y la unidad de trabajo anotan cada
// paso en la misma lista, para que las pruebas puedan fijar el orden entre Storage y el commit de
// Tenancy (decisiones 7 y 8 del spec 2026-09-19).

internal sealed class InMemoryTenantRepository(Tenant tenant) : ITenantRepository
{
    public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken) =>
        Task.FromResult(id == tenant.Id ? tenant : null);

    public void Add(Tenant tenant) => throw new NotSupportedException();
}

/// <summary>Anota <c>commit</c> en <paramref name="steps"/> y, si <see cref="Failure"/> no es null,
/// la lanza en vez de commitear — p. ej. el choque de concurrencia de EF entre dos PUT.</summary>
internal sealed class RecordingTenancyUnitOfWork(List<string> steps) : ITenancyUnitOfWork
{
    public Exception? Failure { get; set; }

    public int Commits { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        steps.Add("commit");
        if (Failure is not null)
        {
            throw Failure;
        }

        Commits++;
        return Task.FromResult(1);
    }

    public Task<IUserLifecycleScope> BeginUserLifecycleScopeAsync(
        string email, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed record LogoStorageCall(string Method, Guid FileId, CancellationToken Token);

/// <summary>Anota cada llamada (método, archivo y token) y cada paso en <paramref name="steps"/>.
/// <see cref="TryUnpublishAsync"/> nunca lanza, igual que el adaptador real: con
/// <see cref="TryUnpublishFails"/> anota el paso como fallido y vuelve.</summary>
internal sealed class RecordingTenantLogoStorage(List<string> steps) : ITenantLogoStorage
{
    public List<LogoStorageCall> Calls { get; } = [];

    /// <summary>La clave que devuelve <see cref="PublishAsync"/>; vacía hace que el agregado
    /// rechace la asignación.</summary>
    public string PublicKey { get; set; } = "tenants/x/media/y/original.png";

    public bool TryUnpublishFails { get; set; }

    /// <summary>Se ejecuta dentro de <see cref="UnpublishAsync"/>, para mirar el estado del
    /// agregado en ese momento.</summary>
    public Action? OnUnpublish { get; set; }

    public Task<TenantLogoPublication> PublishAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Record("publish", fileId, cancellationToken);
        return Task.FromResult(new TenantLogoPublication(PublicKey));
    }

    public Task UnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Record("unpublish", fileId, cancellationToken);
        OnUnpublish?.Invoke();
        return Task.CompletedTask;
    }

    public Task TryUnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Record(TryUnpublishFails ? "try-unpublish-failed" : "try-unpublish", fileId, cancellationToken);
        return Task.CompletedTask;
    }

    public string? GetUrl(string publicKey) => $"https://assets.example.co/{publicKey}";

    private void Record(string method, Guid fileId, CancellationToken token)
    {
        Calls.Add(new LogoStorageCall(method, fileId, token));
        steps.Add($"{method}:{fileId}");
    }
}

internal sealed class RecordingAuditRecorder : IAuditRecorder
{
    public List<string> Actions { get; } = [];

    public void Record(
        Guid? tenantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt,
        AuditActorType actorType = AuditActorType.Human,
        string source = "") =>
        Actions.Add(action);
}

internal sealed class RecordingOutboxWriter : IOutboxWriter
{
    public List<IDomainEvent> Events { get; } = [];

    public void Add(IDomainEvent domainEvent, string correlationId) => Events.Add(domainEvent);
}

internal sealed class SettingsUpdateExecutionContext(TenantId tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = tenantId;

    public bool HasPermission(string permission) => permission == TenancyPermissions.SettingsUpdate;
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
