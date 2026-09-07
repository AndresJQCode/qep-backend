using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record SendQuotationCommand(
    Guid TenantId, Guid QuotationId) : ICommand<QuotationDto>;

public sealed class SendQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationPdfProvider pdfProvider,
    IQuotationPdfStorage pdfStorage,
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

        // Se lee antes de mutar: `Send` deja la cotización en Sent venga de donde venga, así que
        // después de llamarlo ya no hay forma de saber si esto fue el primer envío o un reenvío.
        var isResend = quotation.Status == QuotationStatus.Sent;

        // El documento se genera acá, no lo sube el navegador: así el PDF que recibe el cliente
        // no depende de qué pantalla lo pidió ni de qué versión del frontend estaba abierta.
        // Sólo cuesta una llamada a `qcode-pdf` si la cotización cambió desde la última vez.
        var pdf = await pdfProvider.EnsureCurrentAsync(quotation, cancellationToken);

        // Meta **no puede** bajar el PDF desde una URL prefirmada de R2: le falla y descarta el
        // mensaje entero, minutos después de que Zenvia ya respondió 200. Por eso se publica una
        // copia con clave aleatoria, que además evita que Meta sirva de su caché el documento
        // viejo en un reenvío. La copia la limpia la regla de lifecycle del bucket.
        var documentUrl = await pdfStorage.PublishAsync(pdf.StorageKey, cancellationToken);

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
        quotation.Send(sentBy, now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            isResend ? QuotationHistoryEventType.Resent : QuotationHistoryEventType.Sent,
            sentBy,
            isResend ? QuotationChangeSummary.Resent() : QuotationChangeSummary.Sent(),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            isResend ? "quotation.quotation.resent" : "quotation.quotation.sent",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
