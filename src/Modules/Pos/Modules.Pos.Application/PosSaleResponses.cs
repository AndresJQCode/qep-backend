using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Arma PosSaleResponse —la venta y los datos del ticket— para crear, leer y anular.</summary>
internal static class PosSaleResponses
{
    public static async Task<PosSaleResponse> BuildAsync(
        PosSale sale,
        ICashSessionRepository sessions,
        IPosCashierLookup cashiers,
        ITenantClock tenantClock,
        CancellationToken cancellationToken)
    {
        var session = await sessions.FindAsync(sale.TenantId, sale.CashSessionId, cancellationToken)
            ?? throw new InvalidOperationException($"Sale '{sale.Id}' points to a missing cash session.");
        var calendar = await tenantClock.GetAsync(sale.TenantId, cancellationToken);

        PosVoidResponse? voidInfo = null;
        if (sale.Status == PosSaleStatus.Voided)
        {
            var voidedBy = sale.VoidedBy is { } member
                ? await cashiers.FindNameAsync(sale.TenantId, member.Value, cancellationToken)
                : null;
            voidInfo = new PosVoidResponse(sale.VoidReason!, calendar.ToLocal(sale.VoidedAt!.Value), voidedBy ?? string.Empty);
        }

        var (voidable, blockedReason) = PosVoidability.For(sale.Status, session.Status);

        return new PosSaleResponse(
            sale.Id.Value,
            sale.SaleNumber,
            sale.Status.ToString(),
            sale.CreatedAt,
            calendar.ToLocal(sale.CreatedAt),
            sale.CashSessionId.Value,
            session.CashierName,
            new PosIssuerResponse(session.CompanyName, session.CompanyTaxId, session.CompanyAddress, session.CompanyPhone),
            new PosCustomerResponse(sale.CustomerName, sale.CustomerIdentificationType, sale.CustomerIdentificationNumber),
            sale.Lines
                .OrderBy(line => line.Position)
                .Select(line => new PosSaleLineResponse(
                    line.Position, line.ProductId, line.ProductCode, line.ProductName, line.Quantity, line.UnitPrice,
                    line.DiscountPercentage, line.TaxPercentage, line.DiscountAmount, line.TaxAmount, line.Subtotal,
                    line.LineTotal))
                .ToArray(),
            sale.Subtotal,
            sale.TaxAmount,
            sale.DiscountAmount,
            sale.Total,
            sale.TaxBreakdown()
                .Select(entry => new PosTaxBreakdownResponse(entry.TaxPercentage, entry.Base, entry.TaxAmount))
                .ToArray(),
            sale.Payments
                .OrderBy(payment => payment.Position)
                .Select(payment => new PosPaymentResponse(
                    payment.Method.ToString(), payment.Amount, payment.Tendered, payment.Reference))
                .ToArray(),
            sale.ChangeAmount,
            voidInfo,
            voidable,
            blockedReason);
    }
}
