using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// "Recoger en tienda" viaja en el mismo objeto que <c>billingUsesBusinessName</c>: adentro de
/// <c>parties</c> en el request y en la raiz de la respuesta. Estas pruebas cuidan las dos
/// puntas del contrato con el frontend: que un cliente viejo que no manda el campo siga
/// cotizando con entrega, y que el valor llegue al agregado y vuelva en el DTO.
/// </summary>
public sealed class QuotationStorePickupContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void APartiesRequestWithoutTheFieldMeansDelivery()
    {
        var request = JsonSerializer.Deserialize<QuotationPartiesRequest>(
            """{ "billing": null, "shipping": null }""", Web);

        Assert.NotNull(request);
        Assert.False(request.IsStorePickup);
    }

    [Fact]
    public void APartiesRequestBindsIsStorePickupFromCamelCase()
    {
        var request = JsonSerializer.Deserialize<QuotationPartiesRequest>(
            """{ "billing": null, "shipping": null, "isStorePickup": true }""", Web);

        Assert.NotNull(request);
        Assert.True(request.IsStorePickup);
    }

    [Fact]
    public void TheRequestFlagReachesTheDomainParties()
    {
        var request = new QuotationPartiesRequest(
            Billing: null,
            new QuotationPartyRequest("Bodega", null, null, "Zona Franca", null, null),
            IsStorePickup: true);

        var parties = request.ToDomain();

        Assert.True(parties.IsStorePickup);
    }

    [Fact]
    public void TheDtoCarriesIsStorePickup()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), Guid.CreateVersion7(), "QUO-2026-0001", Guid.CreateVersion7(),
            new MemberId(Guid.CreateVersion7()), null, null, null,
            QuotationParties.Empty with { IsStorePickup = true },
            null, false, false, new MemberId(Guid.CreateVersion7()),
            DateTimeOffset.UtcNow);

        var dto = quotation.ToDto();

        Assert.True(dto.IsStorePickup);
        Assert.Empty(dto.Parties);
    }

    // La respuesta HTTP lo trae siempre, en la raiz y en camelCase, que es lo que lee la
    // pantalla.
    [Fact]
    public async Task TheResponseSerializesIsStorePickupAtTheRoot()
    {
        var dto = Quotation.Create(
            QuotationId.New(), Guid.CreateVersion7(), "QUO-2026-0001", Guid.CreateVersion7(),
            new MemberId(Guid.CreateVersion7()), null, null, null,
            QuotationParties.Empty with { IsStorePickup = true },
            null, false, false, new MemberId(Guid.CreateVersion7()),
            DateTimeOffset.UtcNow).ToDto();

        var response = await new StubQuotationResponseComposer()
            .ComposeAsync(Guid.CreateVersion7(), dto, TestContext.Current.CancellationToken);
        var json = JsonSerializer.SerializeToElement(response, Web);

        Assert.True(json.GetProperty("isStorePickup").GetBoolean());
    }
}
