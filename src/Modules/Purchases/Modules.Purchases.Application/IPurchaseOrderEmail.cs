namespace Modules.Purchases.Application;

/// <summary>
/// Lo que sale hacia el proveedor: su correo, el mismo documento que imprime el PDF (ya formateado con el formato regional del
/// tenant) y el PDF ya generado, para adjuntarlo.
/// </summary>
public sealed record PurchaseOrderEmailRequest(
    string ToAddress, PurchaseOrderPdfDocument Document, byte[] Pdf, string FileName);

/// <summary>Cómo terminó el envío. Un fallo del canal es dato, no excepción: la orden ya quedó enviada.</summary>
public enum PurchaseOrderEmailResult
{
    Sent,
    Failed,
}

/// <summary>El correo de una orden al proveedor; adaptador en Bootstrapper sobre Notifications (Compras no la ve).</summary>
public interface IPurchaseOrderEmail
{
    Task<PurchaseOrderEmailResult> SendAsync(PurchaseOrderEmailRequest request, CancellationToken cancellationToken);
}
