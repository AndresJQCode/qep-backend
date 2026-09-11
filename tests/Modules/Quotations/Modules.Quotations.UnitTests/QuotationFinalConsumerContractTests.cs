using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// "Facturar a consumidor final" viaja igual que <c>isStorePickup</c>: adentro de
/// <c>parties</c> en el request y en la raiz de la respuesta. Estas pruebas cuidan las dos puntas
/// del contrato con el frontend y, ademas, que <c>customerVatSurplus</c> diga el valor efectivo:
/// la pantalla y el PDF imprimen "exento" con solo verlo en true.
/// </summary>
public sealed class QuotationFinalConsumerContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Un frontend que todavia no manda el campo sigue facturando como antes.
    [Fact]
    public void APartiesRequestWithoutTheFieldBillsTheCustomer()
    {
        var request = JsonSerializer.Deserialize<QuotationPartiesRequest>(
            """{ "billing": null, "shipping": null }""", Web);

        Assert.NotNull(request);
        Assert.False(request.BillsToFinalConsumer);
    }

    [Fact]
    public void APartiesRequestBindsBillsToFinalConsumerFromCamelCase()
    {
        var request = JsonSerializer.Deserialize<QuotationPartiesRequest>(
            """{ "billing": null, "shipping": null, "billsToFinalConsumer": true }""", Web);

        Assert.NotNull(request);
        Assert.True(request.BillsToFinalConsumer);
    }

    [Fact]
    public void TheRequestFlagReachesTheDomainParties()
    {
        var request = new QuotationPartiesRequest(null, null, BillsToFinalConsumer: true);

        var parties = request.ToDomain();

        Assert.True(parties.BillsToFinalConsumer);
    }

    [Fact]
    public void TheDtoCarriesBillsToFinalConsumer()
    {
        var dto = FinalConsumerQuotation(customerVatSurplus: false).ToDto();

        Assert.True(dto.BillsToFinalConsumer);
        Assert.False(dto.IsStorePickup);
        Assert.Empty(dto.Parties);
    }

    // Con consumidor final el IVA se cobra aunque el cliente tenga excedente. Si el DTO llevara
    // el snapshot, quote-totals-summary.tsx pintaria "IVA — Exento" con un total que lo incluye.
    [Fact]
    public void TheDtoReportsTheEffectiveVatSurplusNotTheCustomerSnapshot()
    {
        var quotation = FinalConsumerQuotation(customerVatSurplus: true);

        var dto = quotation.ToDto();

        Assert.True(quotation.CustomerVatSurplus);
        Assert.False(dto.CustomerVatSurplus);
    }

    // La respuesta HTTP lo trae siempre, en la raiz y en camelCase, que es lo que lee la
    // pantalla.
    [Fact]
    public async Task TheResponseSerializesBillsToFinalConsumerAtTheRoot()
    {
        var dto = FinalConsumerQuotation(customerVatSurplus: false).ToDto();

        var response = await new StubQuotationResponseComposer()
            .ComposeAsync(Guid.CreateVersion7(), dto, TestContext.Current.CancellationToken);
        var json = JsonSerializer.SerializeToElement(response, Web);

        Assert.True(json.GetProperty("billsToFinalConsumer").GetBoolean());
        Assert.False(json.GetProperty("isStorePickup").GetBoolean());
    }

    private static Quotation FinalConsumerQuotation(bool customerVatSurplus) =>
        Quotation.Create(
            QuotationId.New(), Guid.CreateVersion7(), "QUO-2026-0001", Guid.CreateVersion7(),
            new MemberId(Guid.CreateVersion7()), null, null, null,
            QuotationParties.Empty with { BillsToFinalConsumer = true },
            null, false, customerVatSurplus, new MemberId(Guid.CreateVersion7()),
            DateTimeOffset.UtcNow);
}
