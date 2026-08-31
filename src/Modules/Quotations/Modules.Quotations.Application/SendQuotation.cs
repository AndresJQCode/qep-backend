using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record SendQuotationCommand(
    Guid TenantId, Guid QuotationId, Guid PdfFileId) : ICommand<QuotationDto>;

public sealed class SendQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationFileLookup pdfLookup,
    IQuotationCustomerLookup customerLookup,
    IWhatsAppSender whatsAppSender,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SendQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        SendQuotationCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        await QuotationPdfResolver.ResolveAsync(
            pdfLookup, command.TenantId, command.PdfFileId, cancellationToken);

        var customer = await customerLookup.FindAsync(
            command.TenantId, quotation.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, command.TenantId, quotation.ClientId);

        var sentBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // El WhatsApp se manda antes de tocar el agregado y a propósito: si Zenvia falla, la
        // cotización tiene que seguir en borrador — "Enviar" significa que de verdad llegó, no
        // que quedó marcada como enviada sin que nadie la haya recibido. Así la persona
        // simplemente reintenta el mismo botón en vez de quedar en un estado a medio camino
        // que ningún otro flujo sabe destrabar.
        await whatsAppSender.SendQuotationAsync(
            new WhatsAppQuotationMessage(
                ToPhone: customer!.Phone,
                FullName: customer.Name,
                Address: customer.Address ?? string.Empty,
                OrderNumber: quotation.QuotationNumber,
                OrderId: quotation.Id.ToString()),
            cancellationToken);

        var now = clock.UtcNow;
        quotation.Send(command.PdfFileId, sentBy, now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Sent,
            sentBy,
            details: null,
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.sent",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
