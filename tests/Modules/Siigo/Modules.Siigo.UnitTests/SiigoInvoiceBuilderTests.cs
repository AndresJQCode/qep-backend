using BuildingBlocks.Domain.Taxes;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using static Modules.Siigo.UnitTests.InvoiceTestData;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §6.3 y §6.4: el armador de la factura es código puro. Cada aserción lleva el valor exacto del cuerpo que
/// se mandaría a Siigo.</summary>
public sealed class SiigoInvoiceBuilderTests
{
    private static SiigoBuildResult Build(
        SiigoInvoiceInput? input = null, SiigoSettings? settings = null, IReadOnlyList<SiigoPaymentOverride>? overrides = null) =>
        SiigoInvoiceBuilder.Build(input ?? Order(), settings ?? Settings(), overrides);

    private static SiigoWithholdingInput ReteFuente(decimal amount = 2000m) =>
        new(ReteFuenteRate, "Retefuente 2,5 %", TaxCountries.RetefuenteType, amount);

    private static SiigoWithholdingInput ReteIca(decimal amount = 500m) =>
        new(ReteIcaRate, "ReteICA 9,66", TaxCountries.ReteIcaType, amount);

    [Fact]
    public void ALineWithVatBecomesAnItemWithItsVatTax()
    {
        using var json = Json(Ready(Build()));
        var item = json.RootElement.GetProperty("items")[0];

        Assert.Equal("SKU-1", item.GetProperty("code").GetString());
        Assert.Equal("Producto uno", item.GetProperty("description").GetString());
        Assert.Equal(2m, item.GetProperty("quantity").GetDecimal());
        Assert.Equal(50000m, item.GetProperty("price").GetDecimal());
        Assert.Equal(0m, item.GetProperty("discount").GetDecimal());
        Assert.Equal(1, item.GetProperty("warehouse").GetInt32());
        var taxes = item.GetProperty("taxes");
        Assert.Equal(1, taxes.GetArrayLength());
        Assert.Equal(7001, taxes[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void ThePriceIsBeforeTheDiscountAndRoundedToSixDecimals()
    {
        using var json = Json(Ready(Build()));
        var item = json.RootElement.GetProperty("items")[1];

        Assert.Equal(33333.333333m, item.GetProperty("price").GetDecimal());
        Assert.Equal(10m, item.GetProperty("discount").GetDecimal());
    }

    [Fact]
    public void AZeroRateLineWithoutLinkHasNoTaxes()
    {
        using var json = Json(Ready(Build()));

        Assert.False(json.RootElement.GetProperty("items")[1].TryGetProperty("taxes", out _));
    }

    [Fact]
    public void AZeroRateLineWithItsOwnLinkCarriesTheTax()
    {
        var settings = Settings();
        settings.ReplaceTaxLinks([new SiigoTaxLink(19, "7001"), new SiigoTaxLink(0, "7000")]);

        using var json = Json(Ready(Build(settings: settings)));

        Assert.Equal(7000, json.RootElement.GetProperty("items")[1].GetProperty("taxes")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void ReteFuenteGoesInsideEveryItemAndReteIcaAtInvoiceLevel()
    {
        var input = Order() with
        {
            Withholdings = [ReteFuente(), ReteIca()],
            RetentionAmount = 2500m,
            NetTotal = 206500m,
        };

        using var json = Json(Ready(Build(input)));
        var items = json.RootElement.GetProperty("items");

        // Después del IVA, en cada ítem.
        var first = items[0].GetProperty("taxes");
        Assert.Equal([7001, 8001], first.EnumerateArray().Select(tax => tax.GetProperty("id").GetInt32()));
        var second = items[1].GetProperty("taxes");
        Assert.Equal([8001], second.EnumerateArray().Select(tax => tax.GetProperty("id").GetInt32()));
        var retentions = json.RootElement.GetProperty("retentions");
        Assert.Equal(1, retentions.GetArrayLength());
        Assert.Equal(8002, retentions[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void AnUnsupportedWithholdingTypeIsMissing()
    {
        var input = Order() with
        {
            Withholdings = [new SiigoWithholdingInput(Guid.NewGuid(), "ReteIVA 15 %", "ReteIVA", 100m)],
        };

        var result = Assert.IsType<SiigoBuildResult.NotReady>(Build(input));

        var missing = Assert.Single(result.Missing);
        Assert.Equal("withholding_type_unsupported", missing.Code);
        Assert.Equal("ReteIVA 15 %", missing.Label);
    }

    [Fact]
    public void AnUnlinkedWithholdingIsMissingWithItsName()
    {
        var rate = Guid.NewGuid();
        var input = Order() with { Withholdings = [new SiigoWithholdingInput(rate, "Retefuente 4 %", TaxCountries.RetefuenteType, 10m)] };

        var missing = Assert.Single(Assert.IsType<SiigoBuildResult.NotReady>(Build(input)).Missing);

        Assert.Equal("withholding_unlinked", missing.Code);
        Assert.Equal(rate.ToString(), missing.Reference);
        Assert.Equal("Retefuente 4 %", missing.Label);
    }

    [Fact]
    public void TheSellerIsTheLinkedSiigoUser()
    {
        var settings = Settings();
        settings.ReplaceSellerLinks([new SiigoSellerLink(Advisor, "744", "ANDREA RITUAL")]);

        using var json = Json(Ready(Build(settings: settings)));

        Assert.Equal(744, json.RootElement.GetProperty("seller").GetInt32());
    }

    [Fact]
    public void AnUnlinkedAdvisorIsNotReady()
    {
        var result = Assert.IsType<SiigoBuildResult.NotReady>(Build(settings: Settings(linkAdvisor: false)));

        Assert.Equal(new SiigoMissing("seller_unlinked", Advisor.ToString(), "Andrea Ritual"), Assert.Single(result.Missing));
    }

    [Fact]
    public void AnAdvisorThatNoLongerResolvesIsUnlinkedWithoutLabel()
    {
        var input = Order() with { AdvisorMembershipId = Guid.CreateVersion7(), AdvisorLabel = null };

        var missing = Assert.Single(Assert.IsType<SiigoBuildResult.NotReady>(Build(input)).Missing);

        Assert.Equal(("seller_unlinked", (string?)null), (missing.Code, missing.Label));
    }

    [Fact]
    public void TheUnlinkedSellerIsReportedWithTheOtherMissing()
    {
        var missing = Assert.IsType<SiigoBuildResult.NotReady>(Build(settings: Settings(linkVariants: false, linkAdvisor: false))).Missing;

        Assert.Contains(missing, value => value.Code == "seller_unlinked");
        Assert.Contains(missing, value => value.Code == "variant_unlinked");
    }

    [Fact]
    public void AnUnlinkedVariantIsMissingWithItsName()
    {
        var missing = Assert.Single(
            Assert.IsType<SiigoBuildResult.NotReady>(Build(settings: Settings(linkVariants: false))).Missing,
            value => value.Code == "variant_unlinked" && value.Reference == Line1Variant.ToString());

        Assert.Equal("Producto uno", missing.Label);
    }

    [Fact]
    public void AnUnlinkedVatRateIsMissingWithThePercentage()
    {
        var settings = Settings();
        settings.ReplaceTaxLinks([]);

        var missing = Assert.Single(Assert.IsType<SiigoBuildResult.NotReady>(Build(settings: settings)).Missing);

        Assert.Equal("tax_unlinked", missing.Code);
        Assert.Equal("19", missing.Reference);
    }

    [Fact]
    public void AllMissingAreReportedTogether()
    {
        var settings = SiigoSettings.Create(Guid.NewGuid(), Guid.NewGuid());
        var input = Order() with
        {
            Withholdings = [ReteIca()],
            BilledParty = Company() with { Identification = " ", CityCode = null },
        };

        var missing = Assert.IsType<SiigoBuildResult.NotReady>(Build(input, settings)).Missing;

        Assert.Equal(
            [
                "settings_incomplete:company",
                "settings_incomplete:document",
                $"seller_unlinked:{Advisor}",
                $"variant_unlinked:{Line1Variant}",
                $"variant_unlinked:{Line2Variant}",
                "tax_unlinked:19",
                $"withholding_unlinked:{ReteIcaRate}",
                "customer_identification_missing:",
                "customer_city_missing:",
                $"payment_unlinked:{BankKey}",
            ],
            missing.Select(value => $"{value.Code}:{value.Reference}"));
    }

    [Fact]
    public void ANonCopOrderIsMissingTheCurrency()
    {
        var missing = Assert.IsType<SiigoBuildResult.NotReady>(Build(Order() with { Currency = "USD" })).Missing;

        Assert.Contains(missing, value => value.Code == "currency_unsupported" && value.Reference == "USD");
    }

    [Fact]
    public void FreightGoesAsAnItemByDefault()
    {
        var input = Order() with
        {
            FreightSubtotal = 10000m, FreightTaxAmount = 1900m, FreightTaxPercentage = 19,
            ProductsTotal = 209000m, Total = 220900m, NetTotal = 220900m,
        };

        var drafts = Assert.IsType<SiigoBuildResult.Ready>(Build(input)).Drafts;

        var draft = Assert.Single(drafts);
        using var json = Json(draft);
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("FLETE", items[2].GetProperty("code").GetString());
        Assert.Equal(1m, items[2].GetProperty("quantity").GetDecimal());
        Assert.Equal(10000m, items[2].GetProperty("price").GetDecimal());
        Assert.Equal(7001, items[2].GetProperty("taxes")[0].GetProperty("id").GetInt32());
        Assert.Equal(220900m, json.RootElement.GetProperty("payments").EnumerateArray().Sum(payment => payment.GetProperty("value").GetDecimal()));
    }

    [Fact]
    public void FreightWithoutItsProductIsMissing()
    {
        var settings = Settings();
        settings.UpdateDocument(new SiigoDocumentSettings("24446", "10", "1", false, false, false, null));
        var input = Order() with { FreightSubtotal = 10000m, FreightTaxAmount = 1900m, FreightTaxPercentage = 19, Total = 220900m, NetTotal = 220900m };

        var missing = Assert.IsType<SiigoBuildResult.NotReady>(Build(input, settings)).Missing;

        Assert.Contains(missing, value => value.Code == "freight_product_missing");
    }

    [Fact]
    public void FreightGoesInItsOwnInvoiceWithoutRetentionsWhenConfigured()
    {
        var input = Order() with
        {
            FreightSubtotal = 10000m, FreightTaxAmount = 1900m, FreightTaxPercentage = 19,
            Total = 220900m, RetentionAmount = 2500m, NetTotal = 218400m,
            Withholdings = [ReteFuente(), ReteIca()],
        };

        var drafts = Assert.IsType<SiigoBuildResult.Ready>(Build(input, Settings(freightSeparate: true))).Drafts;

        Assert.Equal([SiigoInvoiceKind.Order, SiigoInvoiceKind.Freight], drafts.Select(draft => draft.Kind));
        using var order = Json(drafts[0]);
        using var freight = Json(drafts[1]);
        Assert.Equal(2, order.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(206500m, PaymentsSum(order));
        Assert.Equal(1, freight.RootElement.GetProperty("items").GetArrayLength());
        var freightItem = freight.RootElement.GetProperty("items")[0];
        Assert.Equal("FLETE", freightItem.GetProperty("code").GetString());
        Assert.Equal(10000m, freightItem.GetProperty("price").GetDecimal());
        // Ninguna retención en la factura de flete: ni en el ítem ni a nivel de factura.
        Assert.Equal([7001], freightItem.GetProperty("taxes").EnumerateArray().Select(tax => tax.GetProperty("id").GetInt32()));
        Assert.False(freight.RootElement.TryGetProperty("retentions", out _));
        Assert.Equal(11900m, PaymentsSum(freight));
        Assert.Equal("Flete del pedido P-1042", freight.RootElement.GetProperty("observations").GetString());
    }

    [Fact]
    public void EachProofIsAPaymentWithTheAccountPaymentType()
    {
        var input = Order() with
        {
            PaymentProofs = [new SiigoPaymentProofInput(ProofA, 100000m), new SiigoPaymentProofInput(ProofB, 9000m)],
        };

        using var json = Json(Ready(Build(input)));
        var payments = json.RootElement.GetProperty("payments");

        Assert.Equal(3, payments.GetArrayLength());
        Assert.Equal((9001, 100000m), (payments[0].GetProperty("id").GetInt32(), payments[0].GetProperty("value").GetDecimal()));
        Assert.Equal((9001, 9000m), (payments[1].GetProperty("id").GetInt32(), payments[1].GetProperty("value").GetDecimal()));
    }

    [Fact]
    public void TheBalanceGoesToTheAccountPaymentTypeDueOnTheInvoiceDate()
    {
        var input = Order() with { PaymentProofs = [new SiigoPaymentProofInput(ProofA, 100000m)] };

        using var json = Json(Ready(Build(input)));
        var payments = json.RootElement.GetProperty("payments");
        var balance = payments[1];

        // Spec 2026-11-20: el saldo usa la forma de pago de la cuenta de cobro, igual que los comprobantes, y vence el día de la factura.
        Assert.False(payments[0].TryGetProperty("due_date", out _));
        Assert.Equal(9001, balance.GetProperty("id").GetInt32());
        Assert.Equal(109000m, balance.GetProperty("value").GetDecimal());
        Assert.Equal("2026-11-18", balance.GetProperty("due_date").GetString());
    }

    [Fact]
    public void ACashOnDeliveryBalanceAlsoGoesToTheAccountPaymentType()
    {
        var input = Order() with { CashOnDelivery = true, PaymentProofs = [new SiigoPaymentProofInput(ProofA, 100000m)] };

        using var json = Json(Ready(Build(input)));
        var balance = json.RootElement.GetProperty("payments")[1];

        Assert.Equal(9001, balance.GetProperty("id").GetInt32());
        Assert.Equal(109000m, balance.GetProperty("value").GetDecimal());
        Assert.Equal("2026-11-18", balance.GetProperty("due_date").GetString());
    }

    [Fact]
    public void CreditAndCashOnDeliveryNotConfiguredNoLongerMakeAnythingMissing()
    {
        var settings = Settings();
        settings.UpdatePayments(new SiigoPaymentSettings([new SiigoPaymentLink(BankKey, "9001")], null, null, null, null));

        Assert.IsType<SiigoBuildResult.Ready>(Build(settings: settings));
        Assert.IsType<SiigoBuildResult.Ready>(Build(Order() with { CashOnDelivery = true }, settings));
    }

    [Fact]
    public void AnOrderPaidInFullHasNoBalancePayment()
    {
        var input = Order() with { PaymentProofs = [new SiigoPaymentProofInput(ProofA, 209000m)] };

        using var json = Json(Ready(Build(input)));

        Assert.Equal(1, json.RootElement.GetProperty("payments").GetArrayLength());
    }

    [Fact]
    public void AnOrderWithoutAnAccountIsUnlinkedWithNoReferenceEvenWithADefaultConfigured()
    {
        var input = Order() with { BillingAccountKey = null, PaymentProofs = [new SiigoPaymentProofInput(ProofA, 209000m)] };

        var missing = Assert.Single(Assert.IsType<SiigoBuildResult.NotReady>(Build(input)).Missing);

        Assert.Equal("payment_unlinked", missing.Code);
        Assert.Null(missing.Reference);
    }

    [Fact]
    public void AnUnlinkedAccountIsReportedOnceForProofsAndBalance()
    {
        var input = Order() with
        {
            BillingAccountKey = "BANCO RENOMBRADO|000123456",
            PaymentProofs = [new SiigoPaymentProofInput(ProofA, 100000m)],
        };

        var missing = Assert.IsType<SiigoBuildResult.NotReady>(Build(input)).Missing;

        Assert.Single(missing, value => value.Code == "payment_unlinked");
    }

    [Fact]
    public void ProofsFillTheOrderInvoiceBeforeTheFreightInvoice()
    {
        var input = Order() with
        {
            FreightSubtotal = 10000m, FreightTaxAmount = 1900m, FreightTaxPercentage = 19, Total = 220900m, NetTotal = 220900m,
            PaymentProofs = [new SiigoPaymentProofInput(ProofA, 215000m)],
        };

        var drafts = Assert.IsType<SiigoBuildResult.Ready>(Build(input, Settings(freightSeparate: true))).Drafts;
        using var order = Json(drafts[0]);
        using var freight = Json(drafts[1]);

        // 209.000 cubren el pedido y los 6.000 que sobran se van a la de flete; el saldo del flete (5.900) va a la misma forma de pago.
        var orderPayments = order.RootElement.GetProperty("payments");
        Assert.Equal(1, orderPayments.GetArrayLength());
        Assert.Equal(209000m, orderPayments[0].GetProperty("value").GetDecimal());
        var freightPayments = freight.RootElement.GetProperty("payments");
        Assert.Equal([(9001, 6000m), (9001, 5900m)], freightPayments.EnumerateArray().Select(payment => (payment.GetProperty("id").GetInt32(), payment.GetProperty("value").GetDecimal())));
    }

    // Números reales de un pedido: 102 × 137.600 con 35 % de descuento. QEP saca el IVA como bruto − neto (1.456.594,29); Siigo lo redondea
    // desde la base ya redondeada (1.456.594,28), así que su total es un centavo menor y los pagos tienen que sumar ESE total.
    private static SiigoInvoiceInput VatCentOrder(params SiigoPaymentProofInput[] proofs) =>
        Order() with
        {
            Lines = [new SiigoInvoiceLineInput(Line1Variant, "Producto uno", 102m, 7666285.71m, 35m, 19, 1456594.29m)],
            ProductsTotal = 9122880.00m, Total = 9122880.00m, NetTotal = 9122880.00m,
            PaymentProofs = proofs,
        };

    private static (int Id, decimal Value)[] PaymentsOf(System.Text.Json.JsonDocument json) =>
        [.. json.RootElement.GetProperty("payments").EnumerateArray()
            .Select(payment => (payment.GetProperty("id").GetInt32(), payment.GetProperty("value").GetDecimal()))];

    [Fact]
    public void ThePaymentsAddUpToTheTotalSiigoComputesWhenTheVatRoundsDifferently()
    {
        // El comprobante es el neto de QEP completo (9.122.880,00): pasa por un centavo del total de Siigo y se recorta a él.
        using var json = Json(Ready(Build(VatCentOrder(new SiigoPaymentProofInput(ProofA, 9122880.00m)))));

        Assert.Equal([(9001, 9122879.99m)], PaymentsOf(json));
    }

    [Fact]
    public void ACentOfRoundingIsFoldedIntoTheProofInsteadOfASecondBalancePayment()
    {
        // El comprobante quedó en pesos enteros: lo que falta (0,99) es redondeo, no un saldo. Va dentro del comprobante, en un solo pago.
        using var json = Json(Ready(Build(VatCentOrder(new SiigoPaymentProofInput(ProofA, 9122879.00m)))));

        var payments = json.RootElement.GetProperty("payments");
        Assert.Equal(1, payments.GetArrayLength());
        Assert.Equal(9122879.99m, payments[0].GetProperty("value").GetDecimal());
        Assert.False(payments[0].TryGetProperty("due_date", out _));
    }

    [Fact]
    public void AnOrderWithoutProofsStillGetsItsBalanceAsTheSiigoComputedTotal()
    {
        using var json = Json(Ready(Build(VatCentOrder())));

        Assert.Equal([(9001, 9122879.99m)], PaymentsOf(json));
        Assert.Equal("2026-11-18", json.RootElement.GetProperty("payments")[0].GetProperty("due_date").GetString());
    }

    [Fact]
    public void ProofsAboveTheNetAreRejected()
    {
        var input = Order() with { PaymentProofs = [new SiigoPaymentProofInput(ProofA, 209001.01m)] };

        var exception = Assert.Throws<SiigoDomainException>(() => Build(input));

        Assert.Equal("siigo.invoice.payments_exceed_total", exception.Code);
    }

    [Fact]
    public void AnOverrideChangesOneProofPaymentType()
    {
        var input = Order() with
        {
            PaymentProofs = [new SiigoPaymentProofInput(ProofA, 100000m), new SiigoPaymentProofInput(ProofB, 9000m)],
        };

        using var json = Json(Ready(Build(input, overrides: [new SiigoPaymentOverride(SiigoInvoiceKind.Order, ProofB, "9777")])));
        var payments = json.RootElement.GetProperty("payments");

        Assert.Equal(9001, payments[0].GetProperty("id").GetInt32());
        Assert.Equal(9777, payments[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public void AnOverrideForAProofOfAnotherOrderIsAMismatch()
    {
        var exception = Assert.Throws<SiigoDomainException>(
            () => Build(overrides: [new SiigoPaymentOverride(SiigoInvoiceKind.Order, Guid.NewGuid(), "9777")]));

        Assert.Equal("siigo.invoice.payments_mismatch", exception.Code);
    }

    [Fact]
    public void ARenamedBankAccountIsReportedAsUnlinked()
    {
        // Review Focus 3: el pedido guardó la foto de la cuenta con otro nombre de banco; ya no casa con ningún vínculo.
        var input = Order() with
        {
            BillingAccountKey = "BANCO RENOMBRADO|000123456",
            PaymentProofs = [new SiigoPaymentProofInput(ProofA, 100000m)],
        };

        var missing = Assert.Single(Assert.IsType<SiigoBuildResult.NotReady>(Build(input)).Missing);

        Assert.Equal("payment_unlinked", missing.Code);
        Assert.Equal("BANCO RENOMBRADO|000123456", missing.Reference);
    }

    [Fact]
    public void AnItemsTotalOffByMoreThanOnePesoIsATotalsMismatch()
    {
        var result = Assert.IsType<SiigoBuildResult.TotalsMismatch>(Build(Order() with { ProductsTotal = 209002m, Total = 209002m, NetTotal = 209002m }));

        Assert.Equal(209002m, result.QepTotal);
        Assert.Equal(209000m, result.ComputedTotal);
    }

    [Fact]
    public void ADifferenceOfOnePesoPasses()
    {
        Assert.IsType<SiigoBuildResult.Ready>(Build(Order() with { ProductsTotal = 209001m, Total = 209001m, NetTotal = 209001m }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StampSendFollowsTheConnectionSwitch(bool sendToDian)
    {
        using var json = Json(Ready(Build(settings: Settings(sendToDian: sendToDian, sendMail: sendToDian))));

        Assert.Equal(sendToDian, json.RootElement.GetProperty("stamp").GetProperty("send").GetBoolean());
        Assert.Equal(sendToDian, json.RootElement.GetProperty("mail").GetProperty("send").GetBoolean());
    }

    [Fact]
    public void TheHeaderCarriesTheDocumentSellerAndDateButNoPurchaseOrder()
    {
        var input = Order() with { BuyerNotes = "Entregar en portería" };

        using var json = Json(Ready(Build(input)));
        var root = json.RootElement;

        Assert.Equal(24446, root.GetProperty("document").GetProperty("id").GetInt32());
        Assert.Equal(629, root.GetProperty("seller").GetInt32());
        Assert.Equal(10, root.GetProperty("cost_center").GetInt32());
        Assert.Equal("2026-11-18", root.GetProperty("date").GetString());
        Assert.Equal("Pedido P-1042. Entregar en portería", root.GetProperty("observations").GetString());
        // Siigo rechaza `additional_fields.purchase_order` con `document_settings` si el comprobante no lo tiene configurado (prueba real, 2026-11-20).
        Assert.False(root.TryGetProperty("additional_fields", out _));
        Assert.Equal("900123456", root.GetProperty("customer").GetProperty("identification").GetString());
        Assert.Equal(0, root.GetProperty("customer").GetProperty("branch_office").GetInt32());
    }

    [Fact]
    public void TheObservationsCarryTheShippingAddressWhenTheOrderHasOne()
    {
        var input = Order() with { BuyerNotes = "Entregar en portería", ShippingAddress = "Calle 1 # 2-3", ShippingCity = "Bogotá" };

        using var json = Json(Ready(Build(input)));

        Assert.Equal(
            "Pedido P-1042, Importante!! Dirección de despacho: Calle 1 # 2-3, Bogotá",
            json.RootElement.GetProperty("observations").GetString());
    }

    [Fact]
    public void TheObservationsDropTheCityWhenTheShippingPartyHasNone()
    {
        var input = Order() with { ShippingAddress = "Calle 1 # 2-3", ShippingCity = null };

        using var json = Json(Ready(Build(input)));

        Assert.Equal(
            "Pedido P-1042, Importante!! Dirección de despacho: Calle 1 # 2-3",
            json.RootElement.GetProperty("observations").GetString());
    }

    [Fact]
    public void TheObservationsAreCutAtFourThousandCharacters()
    {
        var input = Order() with { BuyerNotes = new string('x', 5000) };

        using var json = Json(Ready(Build(input)));

        Assert.Equal(4000, json.RootElement.GetProperty("observations").GetString()!.Length);
    }

    [Fact]
    public void ADraftWithANewCustomerCarriesItsRequest()
    {
        var draft = Ready(Build());

        Assert.Equal("900123456", draft.NewCustomer!.Identification);
        Assert.Equal("31", draft.NewCustomer.IdTypeCode);
    }

    [Fact]
    public void TheSameInputGivesTheSameBytes()
    {
        Assert.Equal(Ready(Build()).Request.Json, Ready(Build()).Request.Json);
    }

    [Fact]
    public void TheRequestNeverPrintsItsBody()
    {
        Assert.DoesNotContain("900123456", Ready(Build()).ToString(), StringComparison.Ordinal);
    }

    private static decimal PaymentsSum(System.Text.Json.JsonDocument json) =>
        json.RootElement.GetProperty("payments").EnumerateArray().Sum(payment => payment.GetProperty("value").GetDecimal());
}
