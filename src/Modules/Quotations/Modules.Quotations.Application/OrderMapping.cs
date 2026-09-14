using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal static class OrderMapping
{
    public static OrderDto ToDto(this Order order) => new(
        order.Id.Value,
        order.OrderNumber,
        order.QuotationId.Value,
        order.Status.ToString(),
        order.PaymentStatus.ToString(),
        order.Notes,
        order.ConvertedAt,
        order.ConvertedBy.Value,
        order.ApprovedAt,
        order.ApprovedBy?.Value,
        order.RitualCollectionSyncId,
        order.CreatedAt,
        order.UpdatedAt,
        order.PaymentProofs.Select(ToDto).ToArray());

    /// <summary>La fila del listado, con el nombre del cliente y el correo de la asesora ya
    /// resueltos por el handler para toda la pagina de una vez.</summary>
    public static OrderListItemDto ToListItemDto(
        this OrderWithQuotation row, string? clientName, string? advisorEmail) => new(
        row.Order.Id.Value,
        row.Order.OrderNumber,
        row.Quotation.Id.Value,
        row.Quotation.QuotationNumber,
        row.Quotation.ClientId,
        clientName,
        row.Quotation.AdvisorId.Value,
        advisorEmail,
        row.Order.Status.ToString(),
        row.Order.PaymentStatus.ToString(),
        row.Quotation.PaymentMethod,
        row.Order.ConvertedAt,
        row.Quotation.Currency.ToCode(),
        row.Quotation.Total);

    private static OrderPaymentProofDto ToDto(OrderPaymentProof proof) => new(
        proof.Id.Value, proof.FileId, proof.Amount, proof.UploadedAt);
}
