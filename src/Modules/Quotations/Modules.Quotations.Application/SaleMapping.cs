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

    private static SalePaymentProofDto ToDto(SalePaymentProof proof) => new(
        proof.Id.Value, proof.FileId, proof.Amount, proof.UploadedAt);
}
