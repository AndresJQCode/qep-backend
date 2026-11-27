using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record MarkQuotationSentCommand(Guid TenantId, Guid QuotationId) : ICommand<QuotationDto>;

/// <summary>
/// Marca la cotización como enviada por fuera de QEP (spec 2026-11-17): el asesor ya se la mandó al cliente por
/// otro medio —correo, otro WhatsApp, en persona— y sin esto quedaría en borrador para siempre, fuera del
/// vencimiento, del embudo y de los reportes de enviadas.
///
/// Es el mismo paso a <c>Sent</c> que <see cref="SendQuotationHandler"/>, sin canal, sin destinatario y sin
/// publicar el PDF. «Enviada» aquí depende de lo que dice el asesor: QEP no entregó nada y no puede comprobarlo.
/// Por eso queda en el historial como <see cref="QuotationHistoryEventType.MarkedSent"/> y no como
/// <see cref="QuotationHistoryEventType.Sent"/>.
///
/// Sin <c>try/catch</c> ni registro de fallas: no hay efecto externo que se pueda caer a mitad de camino, así
/// que un error sube tal cual y la transacción se descarta.
/// </summary>
public sealed class MarkQuotationSentHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationLifecycleEventPublisher lifecycleEvents,
    IQuotationPdfProvider pdfProvider,
    IQuotationCustomerLookup customerLookup,
    IQuotationProductLookup productLookup,
    ITenantDirectory tenantDirectory,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    ITenantModules tenantModules)
    : ICommandHandler<MarkQuotationSentCommand, QuotationDto>
{
    public const string AuditAction = "quotation.quotation.marked_sent";

    public async Task<QuotationDto> HandleAsync(
        MarkQuotationSentCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);
        await ProjectModuleGuard.EnsureAsync(
            tenantModules, command.TenantId, quotation.Kind, cancellationToken);

        // Las mismas precondiciones que el envío por WhatsApp, y antes de pagar el PDF: una cotización anulada,
        // vencida o convertida no se marca enviada. `Send` vuelve a comprobarlo al final.
        quotation.EnsureSendable();
        await BillingPersonTypeRequirement.EnsureForQuotationAsync(
            tenantDirectory, command.TenantId, quotation, cancellationToken);

        var sentBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var customer = await customerLookup.FindAsync(
            command.TenantId, quotation.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, command.TenantId, quotation.ClientId);

        // El catálogo de hoy para las líneas que se congelan, igual que en el envío: marcarla enviada también
        // es salir del borrador.
        var products = await QuotationItemProductLabel.ResolveMissingAsync(
            productLookup, command.TenantId, quotation, cancellationToken);

        // El PDF se deja al día aunque no salga por QEP: es el documento que el asesor descarga para mandarlo, y
        // el que se lee después como «lo que se le envió».
        await pdfProvider.EnsureCurrentAsync(quotation, cancellationToken);

        var now = clock.UtcNow;
        quotation.Send(sentBy, now, products);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.MarkedSent,
            sentBy,
            QuotationChangeSummary.MarkedSent(),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            AuditAction,
            quotation.Id.ToString(),
            "success",
            now);
        // Spec 2026-10-13 §4.5: Pipeline mueve el negocio con el mismo evento del envío, en el mismo SaveChanges.
        lifecycleEvents.PublishSent(quotation, now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
