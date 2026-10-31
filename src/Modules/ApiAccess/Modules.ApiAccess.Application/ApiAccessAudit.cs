using Modules.Audit.Domain;

namespace Modules.ApiAccess.Application;

/// <summary>
/// Auditoría atómica de ApiAccess (ADR 0019): la entrada se acumula en <c>ApiAccessDbContext</c> y
/// commitea con el cambio. No es el <c>IAuditRecorder</c> compartido, que ya está ligado a
/// <c>TenancyDbContext</c>. <paramref name="changedFields"/> va por nombre de campo, nunca por valor.
/// </summary>
public interface IApiAccessAuditRecorder
{
    void Record(
        Guid tenantId,
        Guid actorId,
        AuditActorType actorType,
        string action,
        Guid apiKeyId,
        string outcome,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset occurredAt);
}

public static class ApiKeyAuditActions
{
    public const string ResourceType = "api_key";
    public const string Created = "api_access.key.created";
    public const string Revoked = "api_access.key.revoked";
    public const string Success = "success";
}
