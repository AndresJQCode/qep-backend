using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PDF del pedido es el mismo documento que el de la cotización (owner: «layout único para
/// cotización y pedido»), armado desde la misma respuesta que dibuja la pantalla del pedido
/// (<see cref="OrderDetailResponse"/>). Lo que cambia es el nombre del documento y que no hay vigencia.
/// </summary>
public sealed class OrderPdfDocumentMapperTests
{
    private static readonly TenantCalendar Calendar = new(
        new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero),
        TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static QuotationPdfDocument Map(OrderDetailResponse detail, QuotationPdfLogo? logo = null) =>
        QuotationPdfDocumentMapper.FromOrder(detail, Calendar, logo);

    [Fact]
    public void AnOrderIsPrintedAsAnOrderWithoutValidity()
    {
        var document = Map(Detail());

        Assert.Equal(QuotationPdfKind.Order, document.Kind);
        Assert.Equal("PED-2026-0042", document.QuotationNumber);
        Assert.Null(document.ValidUntil);
    }

    // Spec 2026-09-17, punto 7, igual que la cotización: creado el 31 de diciembre a las 23:00 en
    // Bogotá —1 de enero en UTC— el documento dice 31 de diciembre.
    [Fact]
    public void TheIssueDateIsTheDayTheOrderWasCreatedInTheTenantsZone()
    {
        var detail = Detail();
        var document = Map(detail with
        {
            Order = detail.Order with { CreatedAt = new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero) },
        });

        Assert.Equal(new DateOnly(2026, 12, 31), document.IssuedOn);
    }

    [Fact]
    public void MapsTheCustomerAdvisorAndBillingAccount()
    {
        var document = Map(Detail());

        Assert.Equal(
            ("Comercializadora del Norte S.A.S.", "CUC-0042", "3001234567 · compras@ejemplo.co", "Calle 100 #15-20, Bogotá"),
            (document.CustomerName, document.CustomerCuc, document.CustomerContact, document.CustomerLocation));
        Assert.Equal("Ana Pérez", document.AdvisorLabel);
        Assert.NotNull(document.BillingAccount);
        Assert.Equal(
            ("Ferretería Andina S.A.S.", "Bancolombia", "123-456789-01"),
            (document.BillingAccount.CompanyName, document.BillingAccount.BankName, document.BillingAccount.AccountNumber));
        Assert.Equal("Transferencia", document.PaymentMethod);
    }

    [Fact]
    public void MapsTheLinesAndTheTotalsOfTheOrder()
    {
        var document = Map(Detail());

        var line = Assert.Single(document.Items);
        Assert.Equal(("BRONCEADOR RITUAL DEL SOL", 12m, 35900m, 30515m, 366180m),
            (line.ProductName, line.Quantity, line.UnitPrice, line.DiscountedUnitPrice, line.LineTotal));
        Assert.Equal(("COP", "$", 0), (document.Currency, document.Symbol, document.Decimals));
        Assert.Equal(
            (307714.29m, 64620m, 19m, 58465.71m, 381180m, 0m, 381180m),
            (document.Subtotal, document.DiscountAmount, document.TaxPercentage, document.TaxAmount, document.Total,
                document.RetentionAmount, document.NetTotal));
    }

    [Fact]
    public void MapsTheShippingOfTheOrder()
    {
        var document = Map(Detail());

        Assert.Equal(
            ("Servientrega", "Charged", 15000m, 19, 12605.04m, 2394.96m, true),
            (document.CarrierName, document.FreightChargeMode, document.FreightAmount, document.FreightTaxPercentage,
                document.FreightSubtotal, document.FreightTaxAmount, document.CashOnDelivery));
    }

    // Spec de Shopify §6.2.5: la tienda nombró un envío que QEP no reconoció como transportadora. El
    // documento imprime ese nombre en vez de callar: sin él no dice cómo viaja el pedido.
    [Fact]
    public void AShopifyOrderWithoutARecognizedCarrierPrintsTheStoreShippingName()
    {
        var detail = Detail();
        var document = Map(detail with
        {
            Order = detail.Order with
            {
                Shipping = detail.Order.Shipping with { Carrier = null, ExternalShippingTitle = "Envío express" },
            },
        });

        Assert.Equal("Envío express", document.CarrierName);
    }

    // Reconocida la transportadora, manda ella: el título de la tienda es lo que había antes de elegirla.
    [Fact]
    public void ARecognizedCarrierWinsOverTheStoreShippingName()
    {
        var detail = Detail();
        var document = Map(detail with
        {
            Order = detail.Order with
            {
                Shipping = detail.Order.Shipping with { ExternalShippingTitle = "Envío express" },
            },
        });

        Assert.Equal("Servientrega", document.CarrierName);
    }

    [Fact]
    public void ABillingPartyWithItsOwnDataIsPrintedAndShippingFallsBackToTheCustomer()
    {
        var detail = Detail();
        var party = new QuotationPartyResponse(
            Guid.CreateVersion7(), "Billing", "Sede administrativa", "1020304050", "6015550198", "fact@ejemplo.co",
            "Carrera 7", null, null);

        var document = Map(detail with { Order = detail.Order with { Parties = [party] } });

        Assert.Equal(
            (false, "Sede administrativa", "1020304050", "6015550198 · fact@ejemplo.co", "Carrera 7"),
            (document.Billing.SameAsCustomer, document.Billing.Name, document.Billing.TaxId, document.Billing.Contact,
                document.Billing.Location));
        Assert.True(document.Shipping.SameAsCustomer);
    }

    [Fact]
    public void AFinalConsumerOrderBillsToTheFixedNameAndTaxId()
    {
        var detail = Detail();

        var document = Map(detail with { Order = detail.Order with { BillsToFinalConsumer = true } });

        Assert.Equal(
            (FinalConsumer.Name, FinalConsumer.IdentificationNumber),
            (document.Billing.Name, document.Billing.TaxId));
    }

    [Fact]
    public void AStorePickupOrderDoesNotFallBackToTheCustomerAddress()
    {
        var detail = Detail();

        var document = Map(detail with { Order = detail.Order with { IsStorePickup = true } });

        Assert.True(document.IsStorePickup);
        Assert.False(document.Shipping.SameAsCustomer);
    }

    // Un pedido de Shopify puede llegar con un cliente que ya no existe o incompleto: el documento sale
    // igual, sin datos del cliente, en vez de reventar.
    [Fact]
    public void SurvivesAnOrderWithoutACustomer()
    {
        var document = Map(Detail() with { Client = null });

        Assert.Equal((string.Empty, string.Empty), (document.CustomerName, document.CustomerContact));
    }

    [Fact]
    public void MapsTheLogo()
    {
        var logo = new QuotationPdfLogo("logo.png", [0x89, 0x50, 0x4E, 0x47]);

        Assert.Same(logo, Map(Detail(), logo).Logo);
    }

    private static OrderDetailResponse Detail()
    {
        var shipping = new OrderShippingResponse(
            false, new ShippingCarrierResponse(Guid.CreateVersion7(), "Servientrega"), "Charged",
            15000m, 19, 12605.04m, 2394.96m, true, null);
        var order = new OrderResponse(
            Guid.CreateVersion7(), "PED-2026-0042", "Manual", null, null, Guid.CreateVersion7(), Guid.CreateVersion7(),
            "Approved", "PaymentPending", "Transferencia", "COP", false, null, false, false, false, null, null, false,
            307714.29m, 19m, 58465.71m, 64620m, 381180m, 0m, 381180m, "Notas internas",
            new QuotationBillingAccountDto(Guid.CreateVersion7(), "Bancolombia", "123-456789-01", "COP"),
            [], new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero), Guid.CreateVersion7(), null, null, null,
            null, null, null, null, null,
            new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero),
            1, [], shipping, 12605.04m, 2394.96m);
        var client = new QuotationClientResponse(
            order.ClientId, "CUC-0042", "Comercializadora del Norte S.A.S.", "3001234567", "compras@ejemplo.co",
            "Calle 100 #15-20", Guid.CreateVersion7(), "Bogotá", Guid.CreateVersion7(), "Cundinamarca", null, false, false,
            true, DateTimeOffset.UtcNow, []);
        var billing = new QuotationBillingResponse(
            Guid.CreateVersion7(), "Ferretería Andina S.A.S.", "900.123.456-7", "Carrera 7 #71-52, Bogotá", "6015550100",
            "Bancolombia", "123-456789-01", "COP");
        // 12 x 35.900 = 430.800; 15% = 64.620; línea = 366.180 con IVA adentro.
        var item = new QuotationItemResponse(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "BRONCEADOR RITUAL DEL SOL", "BRO-01", null, [], 12m, 35900m,
            15m, 64620m, 30515m, 307714.29m, 19, 58465.71m, 1, nameof(QuotationDiscountOrigin.Own));

        return new OrderDetailResponse(order, client, "Ana Pérez", "ana@qep.co", billing, [item], null, [], []);
    }
}
