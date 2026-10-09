namespace Modules.Quotations.Application;

public interface IQuotationSettingsStore
{
    /// <summary><see cref="QuotationSettings.Default"/> when the tenant has no row.</summary>
    Task<QuotationSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Stages the rows on the scoped unit of work; <c>IQuotationsUnitOfWork.SaveChangesAsync</c>
    /// commits them together with the audit outbox row (same transaction, backend CLAUDE.md).</summary>
    Task SaveAsync(Guid tenantId, QuotationSettings settings, CancellationToken cancellationToken);
}
