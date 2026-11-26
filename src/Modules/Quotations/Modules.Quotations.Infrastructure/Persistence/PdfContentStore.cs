using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Infrastructure.Persistence;

internal sealed class PdfContentStore(QuotationsDbContext dbContext) : IPdfContentStore
{
    public async Task<PdfContent> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.PdfSections
            .AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .ToListAsync(cancellationToken);
        var account = await dbContext.PdfPaymentAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);

        // Sin el Create del dominio: lo guardado ya pasó la validación al escribirse, y un token que una fila escrita a mano traiga
        // no puede dejar al tenant sin poder leer su pantalla.
        return new PdfContent(
            SectionOf(rows, PdfSectionKey.PaymentNotice),
            SectionOf(rows, PdfSectionKey.Terms),
            SectionOf(rows, PdfSectionKey.DeliveryProcess),
            account is null ? PdfPaymentAccount.Empty : new PdfPaymentAccount(account.Enabled, account.InQuotation, account.InOrder));
    }

    // Un INSERT … ON CONFLICT por sección, y no filas seguidas por EF: dos primeros guardados a la vez chocarían en la PK con un
    // 500. Así el último en confirmar gana sin error, como en PUT …/quotations/settings. Corre en la transacción abierta del
    // handler, y confirma junto con la auditoría del outbox.
    public async Task SaveAsync(Guid tenantId, PdfContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureTransaction(nameof(SaveAsync));

        foreach (var section in content.All)
        {
            var name = section.Key.ToString();
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO quotations.pdf_sections
                    (tenant_id, section, enabled, in_quotation, in_order, title, content_html, updated_at)
                VALUES
                    ({tenantId}, {name}, {section.Enabled}, {section.InQuotation}, {section.InOrder}, {section.Title},
                     {section.ContentHtml}, now())
                ON CONFLICT (tenant_id, section) DO UPDATE SET
                    enabled = EXCLUDED.enabled,
                    in_quotation = EXCLUDED.in_quotation,
                    in_order = EXCLUDED.in_order,
                    title = EXCLUDED.title,
                    content_html = EXCLUDED.content_html,
                    updated_at = EXCLUDED.updated_at
                """,
                cancellationToken);
        }

        // La cuenta con el mismo upsert que las secciones, por la misma razón.
        var account = content.PaymentAccount;
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quotations.pdf_payment_account (tenant_id, enabled, in_quotation, in_order, updated_at)
            VALUES ({tenantId}, {account.Enabled}, {account.InQuotation}, {account.InOrder}, now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                enabled = EXCLUDED.enabled,
                in_quotation = EXCLUDED.in_quotation,
                in_order = EXCLUDED.in_order,
                updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }

    // Un null en la huella es un PDF vencido (QuotationPdf.IsStaleFor): cada uno se regenera en su próximo pedido, no acá. Corre en
    // la transacción del PUT, así un guardado que falla no deja PDFs vencidos de más.
    public async Task InvalidateQuotationPdfsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        EnsureTransaction(nameof(InvalidateQuotationPdfsAsync));
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE quotations.quotation_pdfs SET content_fingerprint = NULL WHERE tenant_id = {tenantId}",
            cancellationToken);
    }

    private void EnsureTransaction(string method)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"PdfContentStore.{method} must run inside IQuotationsUnitOfWork.BeginTransactionAsync.");
        }
    }

    private static PdfSection SectionOf(List<PdfSectionRow> rows, PdfSectionKey key)
    {
        var name = key.ToString();
        var row = rows.SingleOrDefault(candidate => string.Equals(candidate.Section, name, StringComparison.Ordinal));
        return row is null
            ? PdfSection.Empty(key)
            : new PdfSection(key, row.Enabled, row.InQuotation, row.InOrder, row.Title, row.ContentHtml);
    }
}
