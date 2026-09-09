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
    IQuotationSendFailureLog sendFailureLog,
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
        //
        // Queda **fuera** del try de más abajo a propósito: una cotización anulada o vencida no es
        // un envío que falló, es un envío que nunca empezó, y anotarlo en el registro de fallas lo
        // llenaría de ruido que no se arregla mirando una traza.
        quotation.EnsureSendable();

        // Se lee antes de mutar: `Send` deja la cotización en Sent venga de donde venga, así que
        // después de llamarlo ya no hay forma de saber si esto fue el primer envío o un reenvío.
        var isResend = quotation.Status == QuotationStatus.Sent;

        // El paso en curso. Se va moviendo para que la falla diga **dónde** se cayó, que es lo
        // único que separa "hay que revisar el teléfono del cliente" de "está caído Zenvia".
        var stage = QuotationSendStage.Advisor;
        MemberId? sentBy = null;

        try
        {
            // Se resuelve primero, antes de generar nada: si quien envía no es miembro del
            // tenant, es mejor saberlo antes de pagar una llamada a `qcode-pdf` — y además deja
            // el dato disponible para anotar quién intentó, si algo falla más adelante.
            sentBy = await QuotationAdvisorResolver.ResolveAsync(
                membershipDirectory, executionContext, command.TenantId, cancellationToken);

            // El documento se genera acá, no lo sube el navegador: así el PDF que recibe el
            // cliente no depende de qué pantalla lo pidió ni de qué versión del frontend estaba
            // abierta. Sólo cuesta una llamada a `qcode-pdf` si la cotización cambió.
            stage = QuotationSendStage.Pdf;
            var pdf = await pdfProvider.EnsureCurrentAsync(quotation, cancellationToken);

            // Meta **no puede** bajar el PDF desde una URL prefirmada de R2: le falla y descarta
            // el mensaje entero, minutos después de que Zenvia ya respondió 200. Por eso se
            // publica una copia con clave aleatoria, que además evita que Meta sirva de su caché
            // el documento viejo en un reenvío. La copia la limpia el lifecycle del bucket.
            stage = QuotationSendStage.Publish;
            var documentUrl = await pdfStorage.PublishAsync(pdf.StorageKey, cancellationToken);

            stage = QuotationSendStage.Recipient;
            var customer = await customerLookup.FindAsync(
                command.TenantId, quotation.ClientId, cancellationToken);
            QuotationCustomerEligibility.Ensure(customer, command.TenantId, quotation.ClientId);

            // El WhatsApp se manda antes de tocar el agregado y a propósito: si Zenvia falla, la
            // cotización tiene que seguir en borrador — "Enviar" significa que de verdad llegó, no
            // que quedó marcada como enviada sin que nadie la haya recibido. Así la persona
            // simplemente reintenta el mismo botón en vez de quedar en un estado a medio camino
            // que ningún otro flujo sabe destrabar.
            stage = QuotationSendStage.WhatsApp;
            await whatsAppSender.SendQuotationAsync(
                new WhatsAppQuotationMessage(
                    ToPhone: customer!.Phone,
                    FullName: customer.Name,
                    OrderNumber: quotation.QuotationNumber,
                    Total: quotation.Total,
                    ValidUntil: quotation.ValidUntil!.Value,
                    DocumentUrl: documentUrl),
                cancellationToken);

            stage = QuotationSendStage.Persistence;
            var now = clock.UtcNow;
            quotation.Send(sentBy.Value, now);

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
        catch (Exception exception)
        {
            await RecordFailureAsync(quotation, stage, sentBy);

            // Un error de dominio ya se explica solo: tiene código propio y un mensaje escrito
            // para el caso. Se relanza tal cual, para no esconder
            // `quotation.whatsapp.recipient_missing` detrás de un genérico.
            if (exception is QuotationsDomainException)
            {
                throw;
            }

            // Todo lo demás salía como `500 server.unexpected`: la pantalla decía que algo falló
            // y no había forma de saber qué. Ahora sale con código propio y nombrando el paso.
            // La original viaja como `InnerException`, así que el log de fallas de la API la
            // guarda entera --con su traza-- cuando este error llegue al manejador.
            throw new QuotationsDomainException(
                "quotation.send.failed",
                $"The quotation could not be sent ({stage}).",
                exception);
        }
    }

    /// <summary>
    /// Anota el intento fallido en la línea de tiempo de la cotización, en español y sin detalle
    /// técnico. Fuera de la transacción del request, que se descarta — ver
    /// <see cref="IQuotationSendFailureLog"/>.
    ///
    /// La excepción entera no se guarda acá: la levanta el log de fallas de la API cuando el
    /// error termina de subir, junto con el método, la ruta y el código. Tener las dos mitades en
    /// un solo lugar era duplicar lo que ese log ya hace para todos los endpoints.
    /// </summary>
    private async Task RecordFailureAsync(
        Quotation quotation,
        QuotationSendStage stage,
        MemberId? attemptedBy)
    {
        var historyEntry = QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.SendFailed,
            attemptedBy,
            QuotationChangeSummary.SendFailed(stage),
            clock.UtcNow);

        // `CancellationToken.None` y no el del request: si la falla **fue** una cancelación —el
        // navegador cortó, se venció el timeout—, ese token ya está cancelado y pasarlo dejaría
        // sin anotación justo el caso más difícil de diagnosticar, que es el que motivó todo esto.
        await sendFailureLog.RecordAsync(historyEntry, CancellationToken.None);
    }
}
