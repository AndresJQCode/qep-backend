using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

public sealed record UpdateDistributorCommand(
    Guid TenantId,
    Guid DistributorId,
    long ExpectedVersion,
    Guid PriceListId,
    Guid AdvisorId,
    Guid BillingCompanyId,
    string? Phone = null,
    Guid? WhatsAppConnectionId = null)
    : ICommand<DistributorDetailDto>;

/// <summary>
/// Lista, asesor y empresa facturadora, en cualquier estado. Orden: 403 → 422 → 404 → 412 → las tres verificaciones → 422
/// <c>Phone</c> → 422 conexión. Teléfono y conexión del aviso (spec 2026-11-18) sólo cuentan mientras está invitada (P-2):
/// afuera se ignoran sin 422, porque el formulario reenvía todos los campos. Sin teléfono se borran los dos (P-3). El mismo
/// teléfono con la conexión nula o igual a la guardada no consulta Integrations (D-9): editar no falla porque la conexión
/// dejó de estar lista. Un cambio deja el aviso en <c>null</c> hasta el próximo «Reenviar» (P-3).
/// </summary>
public sealed class UpdateDistributorHandler(
    IExecutionContext executionContext,
    IClock clock,
    IWholesalePriceLists priceLists,
    IDistributorRepository distributors,
    IWholesaleMemberDirectory members,
    IWholesaleCompanyLookup companies,
    ITenantDirectory tenants,
    IWholesalePhoneNormalizer phones,
    IWholesaleWhatsAppConnections whatsAppConnections,
    IWholesaleUnitOfWork unitOfWork,
    IWholesaleAuditPublisher auditPublisher,
    DistributorComposer composer,
    IValidator<UpdateDistributorCommand> validator)
    : ICommandHandler<UpdateDistributorCommand, DistributorDetailDto>
{
    private static readonly string[] WrittenFields = ["priceListId", "advisorId", "billingCompanyId"];

    // Mientras está invitada el PUT también escribe el destino del aviso; el valor (el teléfono) nunca va a la auditoría.
    private static readonly string[] InvitedWrittenFields = [.. WrittenFields, "phone", "whatsAppConnectionId"];

    public async Task<DistributorDetailDto> HandleAsync(UpdateDistributorCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.DistributorManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var distributor = await distributors.GetAsync(command.TenantId, new DistributorId(command.DistributorId), cancellationToken)
            ?? throw WholesaleConflicts.DistributorNotFound(command.DistributorId);
        WholesaleConflicts.EnsureVersion(distributor.Version, command.ExpectedVersion);
        WholesaleConflicts.EnsureAllowed(distributor, DistributorAction.Edit);
        await DistributorTerms.EnsureValidAsync(
            priceLists, members, companies, command.TenantId, command.PriceListId, command.AdvisorId, command.BillingCompanyId,
            cancellationToken);
        var invited = distributor.Status == DistributorStatus.Invited;
        var (phone, connectionId) = invited ? await NoticeTargetAsync(distributor, command, cancellationToken) : (null, null);

        var now = clock.UtcNow;
        if (invited)
        {
            distributor.ChangeNoticeTarget(phone, connectionId);
        }

        distributor.ChangeTerms(new PriceListId(command.PriceListId), command.AdvisorId, command.BillingCompanyId, now);
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            WholesaleAuditActions.DistributorUpdated,
            WholesaleAuditActions.Resources.Distributor,
            distributor.Id.ToString(),
            "success",
            invited ? InvitedWrittenFields : WrittenFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.DetailAsync(distributor, cancellationToken);
    }

    private async Task<(string? Phone, Guid? ConnectionId)> NoticeTargetAsync(
        Distributor distributor, UpdateDistributorCommand command, CancellationToken cancellationToken)
    {
        var phone = await DistributorWhatsAppNotice.NormalizeAsync(tenants, phones, command.TenantId, command.Phone, cancellationToken);
        if (phone is null)
        {
            return (null, null);
        }

        // D-9: nada cambió, nada se resuelve.
        if (phone == distributor.Phone
            && distributor.WhatsAppConnectionId is { } saved
            && (command.WhatsAppConnectionId is null || command.WhatsAppConnectionId == saved))
        {
            return (phone, saved);
        }

        var connectionId = await DistributorWhatsAppNotice.ResolveConnectionAsync(
            whatsAppConnections, command.TenantId, command.WhatsAppConnectionId, cancellationToken);
        return (phone, connectionId);
    }
}
