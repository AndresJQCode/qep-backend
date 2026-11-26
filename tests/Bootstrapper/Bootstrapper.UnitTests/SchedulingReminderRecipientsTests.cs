using Bootstrapper;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using Modules.Scheduling.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan 2026-11-21, PD-7 y Tarea 12: Clientes → Agenda para el recordatorio. El consentimiento vigente es la última fila de
/// <c>customers.data_consents</c> (la ordena el repositorio por <c>(occurred_at, sequence)</c>); el adaptador sólo pregunta si es una
/// revocación. Una revocación por otro medio (<c>Medium = Other</c>, sin identidad de canal) también cuenta: el consentimiento es del
/// cliente, no de una conversación. <c>MarketingOptOut</c> es el de la ficha.
/// </summary>
public sealed class SchedulingReminderRecipientsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Customer NewCustomer(string? phone = "+573001234567") =>
        Customer.Create(
            CustomerId.New(), TenantId, "CLI08000001", "Ana Pérez", null, null, null, null,
            new CustomerContactInfo { Phone = phone, Email = "ana@correo.co", Address = "Calle 1", Country = "CO", CityId = Guid.CreateVersion7() },
            new CustomerCommercialInfo(),
            Now,
            new PassThroughPhones());

    private static async Task<ReminderRecipient?> FindAsync(Customer customer, params CustomerDataConsent[] consents)
    {
        var lookup = new SchedulingReminderRecipients(new FakeCustomerRepository(customer), new FakeConsents(consents));
        return await lookup.FindAsync(TenantId, customer.Id.Value, Ct);
    }

    private static CustomerDataConsent Revocation(Customer customer, DataConsentMedium medium) =>
        CustomerDataConsent.Revocation(
            TenantId, customer.Id, Now, Guid.CreateVersion7(), medium,
            medium == DataConsentMedium.Other ? null : new DataConsentIdentity(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "573001234567", "573001234567"),
            "Pidió no recibir más mensajes");

    [Fact]
    public async Task ACustomerWithoutAnyConsentRowIsReachableByItsPhone()
    {
        var customer = NewCustomer();

        var recipient = await FindAsync(customer);

        Assert.Equal(
            new ReminderRecipient(customer.Id.Value, "Ana Pérez", "+573001234567", IsActive: true, ConsentRevoked: false, MarketingOptOut: false),
            recipient);
    }

    [Theory]
    [InlineData(DataConsentMedium.ThisConversation)]
    // Verificado en el código (CustomerDataConsent.Revocation, CK_data_consents_identity): una revocación por otro medio no lleva identidad
    // de canal y el repositorio lee por cliente, así que también omite el recordatorio.
    [InlineData(DataConsentMedium.Other)]
    public async Task ARevokedCustomerIsFlaggedWhateverTheMediumOfTheRevocation(DataConsentMedium medium)
    {
        var customer = NewCustomer();

        var recipient = await FindAsync(customer, Revocation(customer, medium));

        Assert.True(recipient!.ConsentRevoked);
    }

    [Fact]
    public async Task AnAcceptanceAfterTheRevocationIsTheCurrentStateAndDoesNotBlock()
    {
        var customer = NewCustomer();
        // El repositorio entrega primero la fila más reciente por (occurred_at, sequence): acá, la aceptación.
        var accepted = CustomerDataConsent.FromChatbot(
            TenantId, customer.Id, DataConsentEvent.Accepted, Now.AddDays(1),
            new DataConsentIdentity(Guid.CreateVersion7(), DataConsentChannel.WhatsApp, "573001234567", "573001234567"),
            new ChatbotConsentEvidence("Acepto", Guid.CreateVersion7(), Guid.CreateVersion7(), "wamid.1", Guid.CreateVersion7(), "Bot", Guid.CreateVersion7(), 1, "n1"));

        var recipient = await FindAsync(customer, accepted, Revocation(customer, DataConsentMedium.Other));

        Assert.False(recipient!.ConsentRevoked);
    }

    [Fact]
    public async Task AMarketingOptOutOnTheFileIsFlagged()
    {
        var customer = NewCustomer();
        customer.OptOutOfMarketing(MarketingConsentSource.Button, Now);

        var recipient = await FindAsync(customer);

        Assert.True(recipient!.MarketingOptOut);
        Assert.False(recipient.ConsentRevoked);
    }

    [Fact]
    public async Task ACustomerWithoutAPhoneComesBackWithoutIt()
    {
        // La ficha completa exige teléfono; sin él sólo existe la incompleta, como la de una venta externa.
        var customer = Customer.CreateIncompleteFromExternalSale(
            CustomerId.New(), TenantId, "Ana Pérez", phone: null, "ana@correo.co", "CO", Now, new PassThroughPhones());

        var recipient = await FindAsync(customer);

        Assert.Null(recipient!.PhoneE164);
    }

    [Fact]
    public async Task ACustomerThatDoesNotExistInTheTenantIsNull()
    {
        var customer = NewCustomer();
        var lookup = new SchedulingReminderRecipients(new FakeCustomerRepository(customer), new FakeConsents());

        Assert.Null(await lookup.FindAsync(TenantId, Guid.CreateVersion7(), Ct));
        Assert.Null(await lookup.FindAsync(Guid.CreateVersion7(), customer.Id.Value, Ct));
    }

    private sealed class FakeConsents(params CustomerDataConsent[] ordered) : ICustomerDataConsentRepository
    {
        public Task<IReadOnlyList<CustomerDataConsent>> ListAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerDataConsent>>([.. ordered.Where(consent => consent.CustomerId == customerId)]);

        public Task<CustomerDataConsent?> FindLatestAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
            Task.FromResult(ordered.FirstOrDefault(consent => consent.CustomerId == customerId));

        public Task<bool> ReplyRecordedAsync(Guid tenantId, Guid replyMessageId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> LockCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Add(CustomerDataConsent consent) => throw new NotSupportedException();
    }
}
