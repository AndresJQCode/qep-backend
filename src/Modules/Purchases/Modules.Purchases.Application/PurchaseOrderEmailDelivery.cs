using Modules.Purchases.Domain;

namespace Modules.Purchases.Application;

/// <summary>
/// Un intento de mandar la orden al correo del proveedor, y lo que deja: el resultado en la orden, un evento de auditoría y un
/// segundo guardado. Lo comparten el envío y el reenvío. Nada de lo que falla acá sube como excepción: un correo que no sale no
/// deshace el envío de una orden que ya quedó en <c>Sent</c>, así que el fallo del canal, del PDF o del guardado del resultado se
/// vuelve <see cref="PurchaseOrderEmailStatus.Failed"/> y el llamador lo muestra.
/// </summary>
public sealed class PurchaseOrderEmailDelivery(
    IPurchaseOrderEmail email,
    IPurchaseOrderDocumentBuilder documents,
    IPurchasesUnitOfWork unitOfWork,
    IPurchasesAuditPublisher auditPublisher)
{
    private static readonly string[] EmailField = ["email"];

    /// <param name="detail">El detalle de la orden ya compuesto (el PDF imprime los mismos totales).</param>
    /// <param name="supplier">Con <see cref="PurchasesSupplierRef.Email"/> escrito: quien llama ya resolvió el caso «sin correo».</param>
    public async Task<PurchaseOrderEmailDto> DeliverAsync(
        PurchaseOrder order,
        PurchaseOrderDetailDto detail,
        PurchasesSupplierRef supplier,
        Guid actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(supplier);
        var to = supplier.Email!.Trim();

        var result = PurchaseOrderEmailResult.Failed;
        try
        {
            var bundle = await documents.BuildAsync(order, detail, supplier, cancellationToken);
            result = await email.SendAsync(
                new PurchaseOrderEmailRequest(to, bundle.Document, bundle.Pdf, bundle.FileName), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // La orden ya está enviada: el detalle de la falla vive en el log del adaptador, no acá.
        }

        var status = result == PurchaseOrderEmailResult.Sent ? PurchaseOrderEmailStatus.Sent : PurchaseOrderEmailStatus.Failed;
        order.RecordEmail(status, to, now);
        auditPublisher.Publish(
            order.TenantId,
            actorId,
            status == PurchaseOrderEmailStatus.Sent ? PurchasesAuditActions.EmailSent : PurchasesAuditActions.EmailFailed,
            PurchasesAuditActions.Resources.PurchaseOrder,
            order.Id.ToString(),
            status == PurchaseOrderEmailStatus.Sent ? "success" : "failure",
            EmailField,
            now);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // El correo ya salió o ya falló: la respuesta lleva el resultado aunque no haya quedado guardado.
        }

        return new PurchaseOrderEmailDto(status.ToString(), to, now, to);
    }
}
