using BuildingBlocks.Application;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

/// <summary>Cuerpo de <c>PUT /settings</c>; <c>null</c> quita la conexión elegida.</summary>
public sealed record UpdateBillingSettingsRequest(Guid? WhatsAppConnectionId);

public sealed record UpdateBillingSettingsCommand(Guid TenantId, Guid? WhatsAppConnectionId) : ICommand<BillingSettingsDto>
{
    public static UpdateBillingSettingsCommand From(Guid tenantId, UpdateBillingSettingsRequest request) =>
        new(tenantId, request.WhatsAppConnectionId);
}

/// <summary>
/// Elige la conexión de WhatsApp de los avisos (spec §4). Sin <c>If-Match</c>, como el perfil: la última escritura gana. La
/// conexión tiene que estar entre las que lista el puerto (<c>billing.settings.connection_unknown</c>); si todavía no está
/// <c>Ready</c> se acepta, porque el aviso falla con <c>channel_not_configured</c> y la pantalla ya muestra por qué.
/// </summary>
public sealed class UpdateBillingSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBillingSettingsRepository settingsRepository,
    IBillingWhatsAppOutbound whatsApp,
    IBillingUnitOfWork unitOfWork,
    IBillingAuditPublisher auditPublisher,
    IClock clock)
    : ICommandHandler<UpdateBillingSettingsCommand, BillingSettingsDto>
{
    private static readonly string[] ChangedFields = ["whatsAppConnectionId"];

    public async Task<BillingSettingsDto> HandleAsync(UpdateBillingSettingsCommand command, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, BillingPermissions.Manage, cancellationToken);

        // La lista va acotada al tenant de la ruta: la conexión de otro tenant llega como «desconocida».
        var connections = await whatsApp.ListConnectionsAsync(command.TenantId, cancellationToken);
        if (command.WhatsAppConnectionId is { } connectionId && connections.All(connection => connection.Id != connectionId))
        {
            throw new BillingDomainException(
                BillingErrorCodes.SettingsConnectionUnknown,
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
                BillingAuditActions.SettingsUpdated,
                BillingAuditActions.Resources.Settings,
                command.TenantId.ToString(),
                "success",
                ChangedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new BillingSettingsDto(settings.WhatsAppConnectionId, connections);
    }
}
