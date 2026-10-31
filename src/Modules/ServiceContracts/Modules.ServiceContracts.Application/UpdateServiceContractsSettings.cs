using BuildingBlocks.Application;
using Modules.ServiceContracts.Domain;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

/// <summary>Cuerpo de <c>PUT /settings</c>; <c>null</c> quita la conexión elegida.</summary>
public sealed record UpdateServiceContractsSettingsRequest(Guid? WhatsAppConnectionId);

public sealed record UpdateServiceContractsSettingsCommand(Guid TenantId, Guid? WhatsAppConnectionId) : ICommand<ServiceContractsSettingsDto>
{
    public static UpdateServiceContractsSettingsCommand From(Guid tenantId, UpdateServiceContractsSettingsRequest request) =>
        new(tenantId, request.WhatsAppConnectionId);
}

/// <summary>
/// Elige la conexión de WhatsApp de los avisos (spec §4). Sin <c>If-Match</c>, como el perfil: la última escritura gana. La
/// conexión tiene que estar entre las que lista el puerto (<c>billing.settings.connection_unknown</c>); si todavía no está
/// <c>Ready</c> se acepta, porque el aviso falla con <c>channel_not_configured</c> y la pantalla ya muestra por qué.
/// </summary>
public sealed class UpdateServiceContractsSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractsSettingsRepository settingsRepository,
    IServiceContractsWhatsAppOutbound whatsApp,
    IServiceContractsUnitOfWork unitOfWork,
    IServiceContractsAuditPublisher auditPublisher,
    IClock clock)
    : ICommandHandler<UpdateServiceContractsSettingsCommand, ServiceContractsSettingsDto>
{
    private static readonly string[] ChangedFields = ["whatsAppConnectionId"];

    public async Task<ServiceContractsSettingsDto> HandleAsync(UpdateServiceContractsSettingsCommand command, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ServiceContractsPermissions.Manage, cancellationToken);

        // La lista va acotada al tenant de la ruta: la conexión de otro tenant llega como «desconocida».
        var connections = await whatsApp.ListConnectionsAsync(command.TenantId, cancellationToken);
        if (command.WhatsAppConnectionId is { } connectionId && connections.All(connection => connection.Id != connectionId))
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.SettingsConnectionUnknown,
                $"WhatsApp connection '{connectionId}' is not one of the tenant's connections.");
        }

        var now = clock.UtcNow;
        var settings = await settingsRepository.GetOrDefaultAsync(command.TenantId, now, cancellationToken);
        if (settings.WhatsAppConnectionId != command.WhatsAppConnectionId)
        {
            settings.ChangeWhatsAppConnection(command.WhatsAppConnectionId, now);
            settingsRepository.Upsert(settings);
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                ServiceContractsAuditActions.SettingsUpdated,
                ServiceContractsAuditActions.Resources.Settings,
                command.TenantId.ToString(),
                "success",
                ChangedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new ServiceContractsSettingsDto(settings.WhatsAppConnectionId, connections);
    }
}
