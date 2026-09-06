using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <param name="PdfFileId">
/// El PDF ya subido a Storage, si hay uno. <b>Null es un envío sin documento</b>: la cotización
/// pasa a <c>Sent</c> y no se manda ningún WhatsApp. Es el camino que usa hoy el botón "Enviar" —
/// marcar la cotización como enviada es lo que habilita convertirla en venta, y el documento y su
/// entrega son un paso aparte que no tiene por qué bloquear ese flujo.
/// </param>
public sealed record SendQuotationCommand(
    Guid TenantId, Guid QuotationId, Guid? PdfFileId) : ICommand<QuotationDto>;

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

        var sentBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Sin PDF no hay nada que entregar, así que tampoco hay WhatsApp: la cotización queda
        // marcada como enviada y nada más. El bloque de abajo es el envío completo —documento
        // firmado y mensaje— y sigue disponible para quien mande un `pdfFileId`.
        if (command.PdfFileId is { } pdfFileId)
        {
            await DeliverAsync(command.TenantId, pdfFileId, quotation, cancellationToken);
        }

        var now = clock.UtcNow;
        quotation.Send(command.PdfFileId, sentBy, now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Sent,
            sentBy,
            command.PdfFileId is null
                ? QuotationChangeSummary.SentWithoutDocument()
                : QuotationChangeSummary.Sent(),
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

    /// <summary>
    /// El envío con documento: valida el PDF contra Storage, firma su URL y le entrega el mensaje
    /// a WhatsApp.
    ///
    /// Todo esto pasa **antes** de tocar el agregado y a propósito: si Zenvia falla, la cotización
    /// tiene que seguir en borrador — con documento de por medio, "Enviar" significa que de verdad
    /// llegó, no que quedó marcada como enviada sin que nadie la haya recibido. Así la persona
    /// reintenta el mismo botón en vez de quedar en un estado a medio camino.
    /// </summary>
    private async Task DeliverAsync(
        Guid tenantId,
        Guid pdfFileId,
        Quotation quotation,
        CancellationToken cancellationToken)
    {
        await QuotationPdfResolver.ResolveAsync(
            pdfLookup, tenantId, pdfFileId, cancellationToken);

        // WhatsApp no descarga el PDF con la sesión de nadie: lo baja Meta, desde sus propios
        // servidores, y el bucket es privado. Por eso se firma una URL de vida corta en vez de
        // publicar el archivo — publicarlo lo dejaría accesible para siempre y sin dueño que lo
        // despublique.
        var documentUrl = await pdfLookup.CreateDownloadUrlAsync(
            tenantId,
            pdfFileId,
            $"Cotizacion-{quotation.QuotationNumber}.pdf",
            cancellationToken);

        var customer = await customerLookup.FindAsync(
            tenantId, quotation.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, tenantId, quotation.ClientId);

        await whatsAppSender.SendQuotationAsync(
            new WhatsAppQuotationMessage(
                ToPhone: customer.Phone,
                FullName: customer.Name,
                OrderNumber: quotation.QuotationNumber,
                Total: quotation.Total,
                ValidUntil: quotation.ValidUntil!.Value,
                DocumentUrl: documentUrl),
            cancellationToken);
    }
}
