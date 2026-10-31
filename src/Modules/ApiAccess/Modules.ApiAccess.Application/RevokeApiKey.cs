using BuildingBlocks.Application;
using Modules.ApiAccess.Domain;
using Modules.Audit.Domain;
using Modules.Tenancy.Application;

namespace Modules.ApiAccess.Application;

public sealed record RevokeApiKeyCommand(Guid TenantId, Guid ApiKeyId) : ICommand<ApiKeyDto>;

// Sin validador: el comando no lleva texto libre. Revocar una clave ya revocada no falla y no vuelve a
// auditar: el agregado conserva la primera revocación.
public sealed class RevokeApiKeyHandler(
    IApiKeyRepository repository,
    IApiAccessUnitOfWork unitOfWork,
    IApiAccessAuditRecorder auditRecorder,
    IApiKeyConnectionDirectory connections,
    ApiScopeCatalog scopeCatalog,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IClock clock)
    : ICommandHandler<RevokeApiKeyCommand, ApiKeyDto>
{
    private static readonly string[] RevokedFields = ["revokedAt"];

    public async Task<ApiKeyDto> HandleAsync(
        RevokeApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        await ApiAccessAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ApiAccessPermissions.ApiKeyManage, cancellationToken);

        // La búsqueda filtra por tenant: la clave de otro tenant es indistinguible de una inexistente.
        var key = await repository.FindAsync(command.TenantId, new ApiKeyId(command.ApiKeyId), cancellationToken)
            ?? throw new ResourceNotFoundException(
                ApiAccessErrorCodes.KeyNotFound, $"API key '{command.ApiKeyId}' was not found.");

        var memberId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException(
                "authorization.denied", "The subject does not have an active membership in this tenant.");

        var now = clock.UtcNow;
        if (key.Revoke(memberId, now))
        {
            auditRecorder.Record(
                command.TenantId,
                executionContext.SubjectId,
                AuditActorType.Human,
                ApiKeyAuditActions.Revoked,
                key.Id.Value,
                ApiKeyAuditActions.Success,
                RevokedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var names = await connections.ListAsync(command.TenantId, cancellationToken);
        return key.ToDto(scopeCatalog.All, names, now);
    }
}
