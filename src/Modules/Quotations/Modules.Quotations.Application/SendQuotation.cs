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

        // Antes de cualquier efecto externo: firmar la URL del PDF y entregarle el mensaje a
        // WhatsApp no se deshacen, y una cotización que no puede pasar a Sent no puede haberle
        // llegado al cliente. `Send` vuelve a comprobarlo al final — este llamado es para el
        // orden, no para reemplazar la invariante del agregado.
        quotation.EnsureSendable();

        await QuotationPdfResolver.ResolveAsync(
            pdfLookup, command.TenantId, command.PdfFileId, cancellationToken);

        // WhatsApp no descarga el PDF con la sesión de nadie: lo baja Meta, desde sus propios
        // servidores, y el bucket es privado. Por eso se firma una URL de vida corta en vez de
        // publicar el archivo — publicarlo lo dejaría accesible para siempre y sin dueño que lo
        // despublique.
        var documentUrl = await pdfLookup.CreateDownloadUrlAsync(
            command.TenantId,
            command.PdfFileId,
            $"Cotizacion-{quotation.QuotationNumber}.pdf",
            cancellationToken);

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
                OrderNumber: quotation.QuotationNumber,
                Total: quotation.Total,
                ValidUntil: quotation.ValidUntil!.Value,
                DocumentUrl: documentUrl),
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
