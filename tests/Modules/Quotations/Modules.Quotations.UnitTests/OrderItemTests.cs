using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// La aritmética de OrderItem (precio con IVA, descuento, foto del producto, cantidad inválida) se
// prueba a través de Order en OrderTests: OrderItem.Create es internal, como QuotationItem.Create, y
// sólo el agregado construye líneas.
public sealed class OrderItemTests
{
    private static readonly OrderId OrderId = Domain.OrderId.New();

    [Fact]
    public void APartyIsCompleteOnlyWithItsSixFields()
    {
        var party = OrderParty.Create(OrderId, QuotationPartyRole.Shipping, new QuotationPartyDetails
        {
            Name = "Bodega", Phone = "3001234567", Email = "bodega@x.co", Address = "Cl 1 # 2-3",
            DepartmentId = Guid.CreateVersion7(), CityId = Guid.CreateVersion7(),
        });
        Assert.True(party.IsComplete);

        party.Apply(new QuotationPartyDetails { Name = "Bodega" });
        Assert.False(party.IsComplete);
    }

    [Fact]
    public void AShippingPartyNeverKeepsAnIdentificationNumber()
    {
        var party = OrderParty.Create(OrderId, QuotationPartyRole.Shipping, new QuotationPartyDetails
        {
            Name = "Bodega", IdentificationNumber = "900.123.456-1",
        });

        Assert.Null(party.IdentificationNumber);
        Assert.Equal("900.123.456-1",
            OrderParty.Create(OrderId, QuotationPartyRole.Billing, party.ToDetails() with { IdentificationNumber = "900.123.456-1" }).IdentificationNumber);
    }
}
