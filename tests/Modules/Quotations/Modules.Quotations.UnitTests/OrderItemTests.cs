using BuildingBlocks.Domain.Catalog;
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
            Name = "Bodega",
            Phone = "3001234567",
            Email = "bodega@x.co",
            Address = "Cl 1 # 2-3",
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
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
            Name = "Bodega",
            IdentificationNumber = "900.123.456-1",
        });

        Assert.Null(party.IdentificationNumber);
        Assert.Equal("900.123.456-1",
            OrderParty.Create(OrderId, QuotationPartyRole.Billing, party.ToDetails() with { IdentificationNumber = "900.123.456-1" }).IdentificationNumber);
    }

    // ---- Tipo y unidad en la línea (spec 2026-11-18 §3.4) ----

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
        Country = "CO",
    };

    private static OrderHeader Header() =>
        new(Guid.CreateVersion7(), Advisor, "COP", "Transferencia", false, null, false, []);

    // Convertir copia el tipo y la unidad de la línea de la cotización: el pedido no vuelve al catálogo.
    [Fact]
    public void FromQuotationCopiesKindAndUnit()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", Guid.CreateVersion7(), Advisor, new DateOnly(2026, 12, 1),
            "Transferencia", null, QuotationParties.Empty, Account, "COP", customerVatSurplus: false,
            customerWithholdings: [], MunicipalContext.None, Advisor, Now);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 80_000m, 0m, 19, Advisor, Now,
            ProductKind.Service, SalesUnit.Hour);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 11_900m, 0m, 19, Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, QuotationProductSnapshot.None);

        var order = Order.FromQuotation(
            Domain.OrderId.New(), TenantId, "PED-2026-0001", quotation, null, Advisor, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal(
            [(ProductKind.Service, SalesUnit.Hour), (ProductKind.Physical, SalesUnit.Unit)],
            order.Items.OrderBy(item => item.Position).Select(item => (item.Kind, item.SalesUnit)));
    }

    // Un pedido manual toma el tipo que Application leyó del catálogo y lo deja en la línea.
    [Fact]
    public void AManualLineKeepsTheKindItWasGiven()
    {
        var order = Order.CreateManual(
            Domain.OrderId.New(), TenantId, "PED-2026-0001", Header(), QuotationParties.Empty, Account,
            ShippingDetails.None,
            [new OrderLineInput(
                Guid.CreateVersion7(), Guid.CreateVersion7(), 3m, 80_000m, 0m, 19,
                Kind: ProductKind.Service, SalesUnit: SalesUnit.Day)],
            null, Advisor, [], Now, DateOnly.FromDateTime(Now.UtcDateTime));

        var line = Assert.Single(order.Items);
        Assert.Equal((ProductKind.Service, SalesUnit.Day), (line.Kind, line.SalesUnit));
    }

    // Los pedidos de Shopify nacen físicos y por unidad (spec §3.4): la plataforma no habla de servicios.
    [Fact]
    public void AnExternalLineIsPhysicalByUnit()
    {
        var order = Order.CreateExternal(
            Domain.OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 2m, 11_900m, 0m, 19)],
            OrderPaymentStatus.PaymentPending, null, Advisor, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Shopify, "#1001", ShippingDetails.None, null, null,
            OrderLineCosts.None);

        var line = Assert.Single(order.Items);
        Assert.Equal((ProductKind.Physical, SalesUnit.Unit), (line.Kind, line.SalesUnit));
    }

    // Spec 2026-11-23: el descuento de línea es base sin IVA, como Subtotal: bruto sin IVA − descuento = Subtotal.
    [Fact]
    public void TheLineDiscountIsWithoutVat()
    {
        var order = Order.CreateManual(
            Domain.OrderId.New(), TenantId, "PED-2026-0003", Header(), QuotationParties.Empty, Account,
            ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 200m, 25_000m, 20m, 19)],
            null, Advisor, [], Now, DateOnly.FromDateTime(Now.UtcDateTime));

        var line = Assert.Single(order.Items);
        var gross = 200m * 25_000m;
        var grossWithoutTax = gross - Math.Round(gross * 19m / 119m, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(grossWithoutTax - line.Subtotal, line.DiscountAmount);
        Assert.Equal(840_336.13m, line.DiscountAmount);
        Assert.Equal(line.DiscountAmount, order.DiscountAmount);
    }
}
