using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// «Enviar el pedido por WhatsApp»: el PDF del pedido al cliente, como
/// <see cref="SendQuotationCommand"/> lo hace con la cotización. <paramref name="Recipient"/> es la
/// misma opción cerrada: <c>"Customer"</c> (default) o <c>"Billing"</c>, los datos de facturación
/// propios del pedido. Nunca un teléfono suelto: el destinatario lo resuelve el backend.
/// </summary>
public sealed record SendOrderCommand(
    Guid TenantId, Guid OrderId, string? Recipient = null) : ICommand<OrderDetailDto>;

/// <summary>
/// Responde el detalle entero (<see cref="OrderDetailDto"/>) y no sólo el pedido: la pantalla repinta el
/// historial con la entrada nueva sin pedir nada después.
///
/// Enviar no cambia el pedido —no hay estado «enviado», ni sube <c>Order.Version</c>—: deja una entrada
/// en el historial y una auditoría, nada más. Por eso se manda en cualquier estado salvo anulado.
///
/// Dos cosas que la cotización tiene y el pedido no, a propósito:
/// <list type="bullet">
/// <item>No hay caché del PDF: se genera en cada envío (ver <see cref="OrderPdfProvider"/>).</item>
/// <item>No hay registro propio de envíos fallidos (<see cref="IQuotationSendFailureLog"/>). Allá existe
/// para que la línea de tiempo de una cotización que sigue en borrador explique por qué no salió; acá el
/// pedido no cambia de estado con el envío, y la excepción entera —con el paso en el mensaje— ya la
/// guarda el log de fallas de la API (<c>audit.request_failures</c>).</item>
/// </list>
/// </summary>
public sealed class SendOrderHandler(
    IOrderRepository orders,
    IQuotationRepository quotations,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IOrderResponseComposer composer,
    IOrderPdfProvider pdfProvider,
    IQuotationPdfStorage pdfStorage,
    IQuotationCustomerLookup customerLookup,
    IWhatsAppSender whatsAppSender,
    IWhatsAppChannelDirectory whatsAppChannels,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<SendOrderCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        SendOrderCommand command, CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);

        // Sin rastreo: el pedido no se modifica, sólo se le agrega historial.
        var order = await orders.FindUntrackedByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);

        // Antes de cualquier efecto externo y **fuera** del envoltorio de fallas, igual que
        // `Quotation.EnsureSendable`: un anulado no es un envío que falló, es uno que nunca empezó.
        order.EnsureSendable();

        // También fuera: quien no es miembro del tenant es un 403, no "no se pudo enviar". En la
        // cotización va adentro porque su registro de fallas necesita saber quién intentó; acá no hay
        // ese registro.
        var sentBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Por dónde sale (plan 2026-10-21), también fuera y después de saber quién envía: sin canal, con
        // varios o sin plantilla de pedido, el envío nunca empezó, y saberlo no cuesta un PDF.
        var channel = WhatsAppChannelSelection.ForOrder(
            await whatsAppChannels.ListAvailableAsync(command.TenantId, cancellationToken));

        // El paso en curso, para que la falla diga dónde se cayó: el teléfono del cliente, `qcode-pdf`,
        // R2 o Zenvia se destraban en lados distintos. Los pasos son los de la cotización.
        var stage = QuotationSendStage.Recipient;
        try
        {
            // El destinatario antes que el PDF, al revés que la cotización: es la falla más probable
            // (un dato que alguien tiene que corregir) y resolverlo no cuesta una llamada a `qcode-pdf`.
            var customer = await customerLookup.FindAsync(
                command.TenantId, order.ClientId, cancellationToken);
            // Sólo que exista en este tenant: un pedido de Shopify puede tener un cliente incompleto
            // (sin CUC ni ficha), y para mandarle el documento basta un teléfono. `OrderCustomerEligibility`
            // es la regla para venderle, no para escribirle.
            if (customer is null || customer.TenantId != command.TenantId)
            {
                throw new QuotationsDomainException(
                    "order.order.client_not_found",
                    $"Client '{order.ClientId}' was not found in this tenant.");
            }

            var (recipient, toPhone, fullName) = ResolveRecipient(command.Recipient, order, customer);

            // Desde la misma respuesta que dibuja la pantalla del pedido, por el mismo motivo que la
            // cotización: el documento y la pantalla no pueden decir cosas distintas.
            stage = QuotationSendStage.Pdf;
            var detail = await composer.ComposeAsync(
                command.TenantId,
                await OrderDetails.LoadAsync(orders, quotations, command.TenantId, order, cancellationToken),
                cancellationToken);
            var storageKey = await pdfProvider.RenderAndSaveAsync(
                command.TenantId, order.Id, detail, cancellationToken);

            // Meta no puede bajar el PDF desde una URL prefirmada de R2 (ver IQuotationPdfStorage).
            stage = QuotationSendStage.Publish;
            var documentUrl = await pdfStorage.PublishAsync(storageKey, cancellationToken);

            // El WhatsApp sale antes de guardar, con el mismo criterio que la cotización: la entrada del
            // historial dice que el documento llegó, así que no se escribe si Zenvia falla.
            stage = QuotationSendStage.WhatsApp;
            await whatsAppSender.SendOrderAsync(
                channel,
                new WhatsAppOrderMessage(
                    ToPhone: toPhone,
                    FullName: fullName,
                    OrderNumber: order.OrderNumber,
                    Total: order.Total,
                    Currency: order.Currency,
                    DocumentUrl: documentUrl),
                cancellationToken);

            stage = QuotationSendStage.Persistence;
            var now = clock.UtcNow;
            orders.AddHistoryEntry(OrderHistoryEntry.Create(
                OrderHistoryEntryId.New(),
                order.Id,
                OrderHistoryEventType.Sent,
                sentBy,
                OrderChangeSummary.Sent(recipient),
                now));
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.order.sent",
                order.Id.ToString(),
                "success",
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not QuotationsDomainException)
        {
            // Un error de dominio ya se explica solo y sale tal cual (`order.whatsapp.recipient_missing`
            // no se esconde detrás de un genérico). Lo demás —timeout, R2, la base— sale con código propio
            // nombrando el paso, y la original viaja como InnerException para el log de fallas de la API.
            throw new QuotationsDomainException(
                "order.send.failed",
                $"The order could not be sent ({stage}).",
                exception);
        }

        return await OrderDetails.LoadAsync(orders, quotations, command.TenantId, order, cancellationToken);
    }

    /// <summary>
    /// El teléfono y el nombre a los que va el mensaje, y a quién para el historial. Misma regla que
    /// <c>SendQuotationHandler.ResolveRecipient</c>: sin elección, el cliente; con <c>Billing</c>, los
    /// datos de facturación propios del pedido, y si no los tiene o no tienen teléfono se rechaza en vez
    /// de caer al cliente en silencio.
    /// </summary>
    private static (QuotationRecipient Recipient, string? ToPhone, string FullName) ResolveRecipient(
        string? recipient,
        Order order,
        QuotationCustomerRef customer)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return (QuotationRecipient.Customer, customer.Phone, customer.Name);
        }

        // Por nombre y no con Enum.TryParse, que también acepta "1" y lo leería como Billing.
        var name = Enum.GetNames<QuotationRecipient>()
            .FirstOrDefault(value => string.Equals(value, recipient.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is null || !Enum.TryParse<QuotationRecipient>(name, out var parsed))
        {
            throw new QuotationsDomainException(
                "order.order.recipient_invalid",
                $"'{recipient}' is not a valid order recipient.");
        }

        if (parsed == QuotationRecipient.Customer)
        {
            return (QuotationRecipient.Customer, customer.Phone, customer.Name);
        }

        var billing = order.Billing;
        if (billing is null || string.IsNullOrWhiteSpace(billing.Phone))
        {
            throw new QuotationsDomainException(
                "order.order.billing_phone_missing",
                "The order has no billing phone to send the document to.");
        }

        return (
            QuotationRecipient.Billing,
            billing.Phone,
            string.IsNullOrWhiteSpace(billing.Name) ? customer.Name : billing.Name);
    }
}
