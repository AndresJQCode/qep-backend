using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class CreateQuotationHandlerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly Guid SubjectId = Guid.CreateVersion7();
    private static readonly Guid MembershipId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateReadsTheTenantDefaultCurrencyForTheActiveTenant()
    {
        var currency = new FixedTenantDefaultCurrency("USD");
        var handler = Handler(defaultCurrency: currency);

        var created = await handler.HandleAsync(CreateCommand(billingAccount: null), CancellationToken.None);

        Assert.Equal("USD", created.Currency);
        Assert.Equal([TenantId], currency.RequestedTenantIds);
    }

    private static CreateQuotationCommand CreateCommand(QuotationBillingAccountRequest? billingAccount) =>
        new(TenantId, ClientId, ValidUntil: null, PaymentMethod: null, Notes: null, Parties: null, billingAccount);

    private static CreateQuotationHandler Handler(FixedTenantDefaultCurrency defaultCurrency) =>
        new(
            new StubQuotationRepository(Quotation.Create(
                QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, new MemberId(MembershipId),
                null, null, null, QuotationParties.Empty, null, "COP", false, false,
                new MemberId(MembershipId), Now)),
            new NoOpQuotationsUnitOfWork(),
            new NoOpQuotationAuditPublisher(),
            new StubQuotationCustomerLookup(new QuotationCustomerRef(
                ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
                "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false)),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new SequentialQuotationNumberGenerator(),
            new DefaultNumberingFormats(),
            new StubMembershipDirectory(MembershipId),
            new StubExecutionContext(SubjectId, TenantId),
            new FixedTenantClock(Now),
            defaultCurrency,
            new CreateQuotationValidator());

    private sealed class SequentialQuotationNumberGenerator : IQuotationNumberGenerator
    {
        private long _next;

        public Task<long> NextAsync(Guid tenantId, int year, CancellationToken cancellationToken) =>
            Task.FromResult(++_next);
    }

    private sealed class DefaultNumberingFormats : IDocumentNumberingFormatLookup
    {
        public Task<DocumentNumberFormat> GetAsync(
            Guid tenantId, DocumentNumberType documentType, CancellationToken cancellationToken) =>
            Task.FromResult(DocumentNumberFormat.DefaultFor(documentType));
    }
}
