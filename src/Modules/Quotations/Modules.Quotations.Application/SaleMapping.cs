using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

internal static class SaleMapping
{
    public static SaleDto ToDto(this Sale sale) => new(
        sale.Id.Value,
        sale.SaleNumber,
        sale.QuotationId.Value,
        sale.Status.ToString(),
        sale.PaymentStatus.ToString(),
        sale.Notes,
        sale.ConvertedAt,
        sale.ConvertedBy.Value,
        sale.ApprovedAt,
        sale.ApprovedBy?.Value,
        sale.RitualCollectionSyncId,
        sale.CreatedAt,
        sale.UpdatedAt,
        sale.PaymentProofs.Select(ToDto).ToArray());

    /// <summary>La fila del listado, con el nombre del cliente y el correo de la asesora ya
    /// resueltos por el handler para toda la pagina de una vez.</summary>
    public static SaleListItemDto ToListItemDto(
        this SaleWithQuotation row, string? clientName, string? advisorEmail) => new(
        row.Sale.Id.Value,
        row.Sale.SaleNumber,
        row.Quotation.Id.Value,
        row.Quotation.QuotationNumber,
        row.Quotation.ClientId,
        clientName,
        row.Quotation.AdvisorId.Value,
        advisorEmail,
        row.Sale.Status.ToString(),
        row.Sale.PaymentStatus.ToString(),
        row.Quotation.PaymentMethod,
        row.Sale.ConvertedAt,
        row.Quotation.Currency.ToCode(),
        row.Quotation.Total);

    private static SalePaymentProofDto ToDto(SalePaymentProof proof) => new(
        proof.Id.Value, proof.FileId, proof.Amount, proof.UploadedAt);
}
