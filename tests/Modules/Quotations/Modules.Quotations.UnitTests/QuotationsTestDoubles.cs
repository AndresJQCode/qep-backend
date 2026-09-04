using BuildingBlocks.Application;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.UnitTests;

// Dobles de los puertos que SendQuotationHandler necesita. Registran lo que reciben en vez de
// simularlo: lo que estas pruebas verifican es qué datos salen del handler hacia cada puerto.

internal sealed class RecordingWhatsAppSender : IWhatsAppSender
{
    public WhatsAppQuotationMessage? Sent { get; private set; }

    public Task SendQuotationAsync(
        WhatsAppQuotationMessage message, CancellationToken cancellationToken)
    {
        Sent = message;
        return Task.CompletedTask;
    }
}

internal sealed class StubQuotationFileLookup(string downloadUrl) : IQuotationFileLookup
{
    public string? RequestedFileName { get; private set; }

    public Task<QuotationFileRef?> FindAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
        Task.FromResult<QuotationFileRef?>(
            new QuotationFileRef(fileId, tenantId, "application/pdf", 1024, IsAvailable: true));

    public Task<string> CreateDownloadUrlAsync(
        Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken)
    {
        RequestedFileName = downloadFileName;
        return Task.FromResult(downloadUrl);
    }
}

internal sealed class StubQuotationCustomerLookup(QuotationCustomerRef customer)
    : IQuotationCustomerLookup
{
    public Task<QuotationCustomerRef?> FindAsync(
        Guid tenantId, Guid clientId, CancellationToken cancellationToken) =>
        Task.FromResult<QuotationCustomerRef?>(customer);

    public Task<IReadOnlySet<Guid>> SearchIdsByIdentificationAsync(
        Guid tenantId, string term, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
}

internal sealed class StubQuotationRepository(Quotation quotation) : IQuotationRepository
{
    public Task<Quotation?> FindAsync(
        Guid tenantId, QuotationId quotationId, CancellationToken cancellationToken) =>
        Task.FromResult<Quotation?>(quotation);

    public Task<(IReadOnlyList<Quotation> Items, int Total)> SearchAsync(
        Guid tenantId,
        Guid? clientId,
        IReadOnlyCollection<Guid>? clientIds,
        MemberId? advisorId,
        QuotationStatus? status,
        DateOnly? createdFrom,
        DateOnly? createdTo,
        string? quotationNumber,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<Quotation>, int)>(([quotation], 1));

    public void Add(Quotation quotation)
    {
    }

    public void AddHistoryEntry(QuotationHistoryEntry entry)
    {
    }
}

internal sealed class NoOpQuotationsUnitOfWork : IQuotationsUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}

internal sealed class NoOpQuotationAuditPublisher : IQuotationAuditPublisher
{
    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId,
        string outcome, DateTimeOffset occurredAt)
    {
    }
}

internal sealed class StubMembershipDirectory(Guid membershipId) : IMembershipDirectory
{
    public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>?>([]);

    public Task<Guid?> FindActiveMembershipIdAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(membershipId);

    public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([membershipId]);
}

internal sealed class StubExecutionContext(Guid subjectId, Guid tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = subjectId;

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => true;
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
