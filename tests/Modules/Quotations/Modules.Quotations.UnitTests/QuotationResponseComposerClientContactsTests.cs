using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-11-10 §4.6: la respuesta de la cotización lleva los contactos de la empresa del cliente (<c>clientContacts</c>) para el
/// diálogo de envío. Es el contrato HTTP (<see cref="QuotationResponse"/>), no el DTO interno: lo llena el compositor.
/// </summary>
public sealed class QuotationResponseComposerClientContactsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly QuotationCustomerRef Customer = new(
        ClientId, TenantId, "CUC-001", IsActive: true, "Ferretería El Tornillo",
        "3001234567", "Calle 1 # 2-3", WithRetention: false, VatSurplus: false);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task TheQuotationResponseCarriesTheClientsContacts()
    {
        var laura = new QuotationClientContact(Guid.CreateVersion7(), "Laura Gómez", "3115550101");
        var pedro = new QuotationClientContact(Guid.CreateVersion7(), "Pedro Ríos", "3125550102");

        var response = await ComposeAsync([laura, pedro]);

        Assert.Equal(
            [new ClientContactDto(laura.Id, "Laura Gómez", "3115550101"), new ClientContactDto(pedro.Id, "Pedro Ríos", "3125550102")],
            response.ClientContacts);
        var json = JsonSerializer.SerializeToElement(response, Web);
        Assert.Equal("Laura Gómez", json.GetProperty("clientContacts")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task AClientWithoutContactsCarriesAnEmptyList()
    {
        var response = await ComposeAsync([]);

        Assert.NotNull(response.ClientContacts);
        Assert.Empty(response.ClientContacts);
    }

    [Fact]
    public async Task ContactsWithoutPhoneTravelWithPhoneNull()
    {
        var laura = new QuotationClientContact(Guid.CreateVersion7(), "Laura Gómez", null);

        var response = await ComposeAsync([laura]);

        Assert.Equal(new ClientContactDto(laura.Id, "Laura Gómez", null), Assert.Single(response.ClientContacts));
    }

    [Fact]
    public void ClientResponseCopiesRegions()
    {
        var madrid = new QuotationRegionDto(Guid.CreateVersion7(), "Madrid");
        var barcelona = new QuotationRegionDto(Guid.CreateVersion7(), "Barcelona");
        var customer = Customer with
        {
            Region = madrid,
            Addresses =
            [
                new QuotationCustomerAddressRef(
                    Guid.CreateVersion7(), "Sede", "Calle 2", null, null, "Barcelona", null, null, true, "ES", barcelona),
            ],
        };

        var client = QuotationResponseComposer.ToClientResponse(customer);

        Assert.NotNull(client);
        Assert.Equal(madrid, client.Region);
        Assert.Equal(barcelona, Assert.Single(client.Addresses).Region);
        var json = JsonSerializer.SerializeToElement(client, Web);
        Assert.Equal("Madrid", json.GetProperty("region").GetProperty("name").GetString());
        Assert.Equal("Barcelona", json.GetProperty("addresses")[0].GetProperty("region").GetProperty("name").GetString());
    }

    private static async Task<QuotationResponse> ComposeAsync(QuotationClientContact[] contacts)
    {
        var customers = new StubQuotationCustomerLookup(Customer);
        customers.Contacts.AddRange(contacts);
        var composer = new QuotationResponseComposer(
            customers,
            new StubQuotationAdvisorLookup("asesora@qcode.co", "Asesora Uno"),
            new StubQuotationProductLookup(new Dictionary<Guid, QuotationVariantRef>()),
            new StubQuotationCompanyLookup(new Dictionary<Guid, QuotationCompanyRef>()),
            new FixedQuotationSettingsStore(),
            new StubQuotationWithholdingLookup(),
            new StubQuotationGeographyLookup(new Dictionary<Guid, string>()),
            new FixedTenantClock(DateTimeOffset.UtcNow));
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "QUO-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 11, 30),
            paymentMethod: null, notes: null, QuotationParties.Empty, billingAccount: null, defaultCurrency: "COP",
            customerVatSurplus: false, customerWithholdings: [], MunicipalContext.None, AdvisorId, Now);

        return await composer.ComposeAsync(TenantId, quotation.ToDto(), TestContext.Current.CancellationToken);
    }
}
