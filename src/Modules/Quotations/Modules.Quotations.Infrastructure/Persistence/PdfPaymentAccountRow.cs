namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>Si el PDF de un tenant imprime la cuenta para el pago (spec 2026-11-16 pdf-payment-account, decisión 3). Un tenant sin
/// fila la lee apagada (<c>PdfPaymentAccount.Empty</c>).</summary>
internal sealed class PdfPaymentAccountRow
{
    public Guid TenantId { get; set; }

    public bool Enabled { get; set; }

    public bool InQuotation { get; set; }

    public bool InOrder { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
