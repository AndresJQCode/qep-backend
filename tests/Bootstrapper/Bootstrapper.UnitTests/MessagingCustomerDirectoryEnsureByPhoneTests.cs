using Microsoft.Extensions.Logging.Abstractions;
using Modules.Customers.Application;
using Modules.Messaging.Application;

namespace Bootstrapper.UnitTests;

/// <summary>El adaptador de Messaging a Customers para el historial de coexistencia: el <c>waId</c> son los dígitos y
/// Customers espera el E.164 con «+», así que el adaptador lo agrega y traduce el resultado.</summary>
public sealed class MessagingCustomerDirectoryEnsureByPhoneTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private sealed class RecordingWhatsApp(EnsureOutcome outcome) : ICustomerWhatsAppDirectory
    {
        public Guid CustomerId { get; } = Guid.CreateVersion7();

        public (Guid Tenant, WhatsAppPhoneContact Contact)? Last { get; private set; }

        public Task<EnsuredCustomer> EnsureByPhoneAsync(Guid tenantId, WhatsAppPhoneContact contact, CancellationToken cancellationToken)
        {
            Last = (tenantId, contact);
            return Task.FromResult(new EnsuredCustomer(CustomerId, outcome));
        }

        public Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<CustomerContact> ListAudienceAsync(
            Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AudienceCount> CountAudienceAsync(
            Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkMarketingOptOutAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MarkMarketingOptOutByButtonAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static MessagingCustomerDirectory Adapter(ICustomerWhatsAppDirectory whatsApp) =>
        new(null!, whatsApp, null!, null!, null!, NullLogger<MessagingCustomerDirectory>.Instance);

    [Fact]
    public async Task TheAdapterAddsThePlusAndReportsACreatedCustomer()
    {
        var whatsApp = new RecordingWhatsApp(EnsureOutcome.Created);

        var ensured = await Adapter(whatsApp).EnsureByPhoneAsync(Tenant, "573001112233", "Ana Pérez", TestContext.Current.CancellationToken);

        Assert.Equal(new MessagingEnsuredCustomer(whatsApp.CustomerId, true), ensured);
        Assert.Equal((Tenant, new WhatsAppPhoneContact("+573001112233", "Ana Pérez")), whatsApp.Last);
    }

    [Fact]
    public async Task AnExistingCustomerIsNotReportedAsCreated()
    {
        var whatsApp = new RecordingWhatsApp(EnsureOutcome.Existing);

        var ensured = await Adapter(whatsApp).EnsureByPhoneAsync(Tenant, "573001112233", null, TestContext.Current.CancellationToken);

        Assert.False(ensured.Created);
        Assert.Null(whatsApp.Last!.Value.Contact.Name);
    }
}
