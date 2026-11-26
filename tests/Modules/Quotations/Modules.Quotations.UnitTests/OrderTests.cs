using BuildingBlocks.Domain.Catalog;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

public sealed class OrderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId Advisor = new(Guid.CreateVersion7());
    private static readonly MemberId ConvertedBy = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static QuotationBillingAccount Account => new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "7542",
        Currency = "COP",
        Country = "CO",
    };

    private static OrderHeader Header(bool isRetail = false, bool withRetention = false) =>
        new(ClientId, Advisor, "COP", "Transferencia", isRetail, null, false, TestWithholdings.Customer(withRetention));

    private static OrderLineInput Line(decimal quantity = 2m, decimal unitPrice = 11_900m, decimal discount = 0m) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), quantity, unitPrice, discount, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    // Por defecto: una línea de 2 × 11.900 con IVA del 19 % → subtotal 20.000, IVA 3.800, neto 23.800, y un
    // comprobante de 100.000 que lo cubre: el pago nace «Por verificar» (spec 2026-11-10 §3.2).
    private static Order NewOrder(
        string? notes = null,
        IReadOnlyCollection<OrderPaymentProofInput>? proofs = null,
        IReadOnlyCollection<OrderLineInput>? lines = null,
        OrderHeader? header = null,
        DateOnly? orderDate = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", header ?? Header(), QuotationParties.Empty, Account,
            ShippingDetails.None, lines ?? [Line()], notes, ConvertedBy,
            proofs ?? [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)], Now,
            orderDate ?? DateOnly.FromDateTime(Now.UtcDateTime));

    private static Order PaidOrder(IReadOnlyCollection<OrderPaymentProofInput>? proofs = null)
    {
        var order = NewOrder(proofs: proofs);
        order.MarkPaid(ConvertedBy, requirePaymentProof: false, order.WithoutCosts(), Now);
        return order;
    }

    [Fact]
    public void CreateManualCarriesItsOwnHeaderLinesAndTotals()
    {
        var order = NewOrder(lines: [Line(quantity: 2m, unitPrice: 11_900m)]);

        Assert.Equal((OrderSource.Manual, (QuotationId?)null, (string?)null), (order.Source, order.QuotationId, order.ExternalReference));
        Assert.Equal((ClientId, Advisor, "COP"), (order.ClientId, order.AdvisorId, order.Currency));
        var item = Assert.Single(order.Items);
        Assert.Equal(("ABC", "Aceite", 1), (item.ProductCode, item.ProductName, item.Position));
        Assert.Equal(20_000m, order.Subtotal);
        Assert.Equal(3_800m, order.TaxAmount);
        Assert.Equal(23_800m, order.Total);
        Assert.Equal(23_800m, order.NetTotal);
    }

    // La aritmética de la línea (ver OrderItemTests): 3 × 11.900 con 10 % en pesos enteros.
    [Fact]
    public void ALineComputesTheSameAmountsAsAQuotationLine()
    {
        var order = NewOrder(lines: [Line(quantity: 3m, unitPrice: 11_900m, discount: 10m)]);

        var item = Assert.Single(order.Items);
        Assert.Equal(3_000m, item.DiscountAmount); // sin IVA (spec 2026-11-23); con IVA eran 3.570
        Assert.Equal(27_000m, item.Subtotal);
        Assert.Equal(5_130m, item.TaxAmount);
        Assert.Equal(10_000m, item.UnitPriceWithoutTax);
        Assert.Equal(QuotationDiscountOrigin.Own, item.DiscountOrigin);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALineRejectsANonPositiveQuantity(decimal quantity)
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(lines: [Line(quantity: quantity)]));

        Assert.Equal("order.item.quantity_invalid", error.Code);
    }

    [Fact]
    public void CreateManualRequiresAtLeastOneLine()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => NewOrder(lines: []));

        Assert.Equal("order.order.items_required", error.Code);
    }

    [Fact]
    public void CreateManualRejectsAnIncompleteShippingParty()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.CreateManual(
                OrderId.New(), TenantId, "PED-2026-0001", Header(),
                new QuotationParties(null, new QuotationPartyDetails { Name = "Bodega" }), Account,
                ShippingDetails.None, [Line()], null, ConvertedBy, [], Now,
                orderDate: DateOnly.FromDateTime(Now.UtcDateTime)));

        Assert.Equal("order.shipping.party_incomplete", error.Code);
    }

    [Fact]
    public void CreateManualRejectsABillingPartyWithoutIdentification()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.CreateManual(
                OrderId.New(), TenantId, "PED-2026-0001", Header(),
                new QuotationParties(
                    new QuotationPartyDetails
                    {
                        Name = "Otra S.A.S.",
                        Phone = "1",
                        Email = "a@b.co",
                        Address = "x",
                        DepartmentId = Guid.CreateVersion7(),
                        CityId = Guid.CreateVersion7(),
                    },
                    null, BillingVatSurplus: false),
                Account, ShippingDetails.None, [Line()], null, ConvertedBy, [], Now,
                orderDate: DateOnly.FromDateTime(Now.UtcDateTime)));

        Assert.Equal("order.billing.identification_required", error.Code);
    }

    [Fact]
    public void CreateManualRejectsABillingPartyWithoutIdentificationType()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.CreateManual(
                OrderId.New(), TenantId, "PED-2026-0001", Header(),
                new QuotationParties(
                    new QuotationPartyDetails
                    {
                        Name = "Otra S.A.S.",
                        IdentificationNumber = "9001234561",
                        Phone = "1",
                        Email = "a@b.co",
                        Address = "x",
                        DepartmentId = Guid.CreateVersion7(),
                        CityId = Guid.CreateVersion7(),
                    },
                    null, BillingVatSurplus: false),
                Account, ShippingDetails.None, [Line()], null, ConvertedBy, [], Now,
                orderDate: DateOnly.FromDateTime(Now.UtcDateTime)));

        Assert.Equal("order.billing.identification_required", error.Code);
    }

    [Fact]
    public void ConvertingCarriesTheBillingPartysPersonTypeAndIdentificationType()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, new DateOnly(2026, 11, 1), "Transferencia",
            null,
            new QuotationParties(
                new QuotationPartyDetails
                {
                    Name = "Otra persona",
                    IdentificationNumber = "1020304050",
                    PersonType = PartyPersonType.Natural,
                    IdentificationType = PartyIdentificationType.Ce,
                    Phone = "3001234567",
                    Email = "otra@x.co",
                    Address = "Cl 1",
                    DepartmentId = Guid.CreateVersion7(),
                    CityId = Guid.CreateVersion7(),
                },
                null, BillingVatSurplus: false),
            Account, "COP", customerVatSurplus: false, customerWithholdings: [], MunicipalContext.None, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 5m, 19, Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [quotation.Items.Single().VariantId] = new("ABC", "Aceite"),
        });

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, null,
            ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal(PartyPersonType.Natural, order.Billing?.PersonType);
        Assert.Equal(PartyIdentificationType.Ce, order.Billing?.IdentificationType);
    }

    [Fact]
    public void CreateExternalAcceptsTheSourceAndReference()
    {
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [Line()],
            OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime),
            OrderSource.Shopify, "#1001", ShippingDetails.None, null, null,
            OrderLineCosts.None);

        Assert.Equal((OrderSource.Shopify, "#1001"), (order.Source, order.ExternalReference));
    }

    // Spec de Shopify (vía spec 2026-10-15 §5.4): el pedido externo entra con el estado de pago que avisa la
    // plataforma y sin comprobantes, porque el pago vive allá. La regla del comprobante es de los pedidos
    // que se cobran desde QEP (cotización y manual).
    [Fact]
    public void CreateExternalAcceptsAPaidStatusWithoutProofs()
    {
        var line = Line();
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [line],
            OrderPaymentStatus.FullPaymentReceived, null, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime),
            OrderSource.Shopify, "#1001", ShippingDetails.None, null, null,
            OrderCostFixtures.WithoutCosts([line]));

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Empty(order.PaymentProofs);
    }

    [Fact]
    public void CreateExternalRejectsTheQuotationSource()
    {
        var error = Assert.Throws<QuotationsDomainException>(() => Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [Line()],
            OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Quotation, "#1", ShippingDetails.None, null, null,
            OrderLineCosts.None));

        Assert.Equal("order.order.source_invalid", error.Code);
    }

    // Un pedido que se guarda nunca nace sin cuenta de cobro: sólo el cálculo previo puede (Order.Preview).
    [Fact]
    public void CreateManualRequiresTheBillingAccount()
    {
        Assert.Throws<ArgumentNullException>(() => Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", Header(), QuotationParties.Empty, null!, ShippingDetails.None, [Line()],
            null, ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime)));
    }

    [Fact]
    public void CreateExternalRequiresTheBillingAccount()
    {
        Assert.Throws<ArgumentNullException>(() => Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, null!, [Line()],
            OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime),
            OrderSource.Shopify, "#1001", ShippingDetails.None, null, null,
            OrderLineCosts.None));
    }

    [Fact]
    public void APreviewWithoutAnAccountIsPricedInTheHeaderCurrency()
    {
        var header = Header() with { Currency = "USD" };

        var order = Order.Preview(
            OrderId.New(), TenantId, "PREVIEW", header, QuotationParties.Empty, billingAccount: null,
            ShippingDetails.None, [Line(quantity: 2m, unitPrice: 11_900m)], null, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Null(order.BillingAccount);
        Assert.Equal(("USD", OrderSource.Manual), (order.Currency, order.Source));
        Assert.Equal(2, Assert.Single(order.Items).Quantity);
        Assert.True(order.Total > 0);
    }

    // Con cuenta, el cálculo previo es el de siempre: la moneda la fija la cuenta, no el encabezado.
    [Fact]
    public void APreviewWithAnAccountTakesTheAccountCurrency()
    {
        var account = Account with { Currency = "USD" };

        var order = Order.Preview(
            OrderId.New(), TenantId, "PREVIEW", Header(), QuotationParties.Empty, account, ShippingDetails.None, [Line()],
            null, ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal("USD", order.Currency);
        Assert.Equal(account.CompanyId, order.BillingAccount?.CompanyId);
    }

    [Fact]
    public void RetentionAppliesToTheSubtotalWhenTheCustomerWithholds()
    {
        var order = NewOrder(header: Header(withRetention: true), lines: [Line(quantity: 10m, unitPrice: 11_900m)]);

        Assert.Equal(100_000m, order.Subtotal);
        Assert.Equal(2_500m, order.RetentionAmount);
        Assert.Equal(116_500m, order.NetTotal);
    }

    [Fact]
    public void RecalculatePaymentStatusUsesTheOrdersOwnNetTotal()
    {
        var order = NewOrder(proofs: [], lines: [Line(quantity: 2m, unitPrice: 11_900m)]);
        order.AttachPaymentProofs([new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)], ConvertedBy, Now);

        order.RecalculatePaymentStatus(order.NetTotal, Now);
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);

        order.AttachPaymentProofs([new OrderPaymentProofInput(Guid.CreateVersion7(), 13_800m)], ConvertedBy, Now);
        order.RecalculatePaymentStatus(order.NetTotal, Now);
        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
    }

    [Fact]
    public void AddingAndRemovingLinesRecalculatesAndKeepsTheLastOne()
    {
        var order = NewOrder(proofs: [], lines: [Line()]);
        var first = Assert.Single(order.Items);

        order.AddItem(OrderItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 5_000m, 0m, 19, OrderLineCosts.None, ConvertedBy, Now);
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(23_800m + 5_000m, order.Total);

        order.RemoveItem(first.Id, ConvertedBy, Now);
        var error = Assert.Throws<QuotationsDomainException>(() => order.RemoveItem(order.Items.Single().Id, ConvertedBy, Now));
        Assert.Equal("order.item.last_item_required", error.Code);
    }

    [Fact]
    public void ItemsCannotChangeOnceTheOrderIsPrepared()
    {
        var order = NewOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddItem(OrderItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 5_000m, 0m, 19, OrderLineCosts.None, ConvertedBy, Now));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    // Portado de QuotationProductSnapshotTests (spec 2026-10-15 §4.5): una línea que se suma al pedido
    // no tiene un envío que la congele; se congela al capturar, y las que ya tenían foto no se tocan.
    [Fact]
    public void CaptureProductSnapshotsFreezesOnlyTheLinesWithoutOne()
    {
        var frozen = Guid.CreateVersion7();
        var added = Guid.CreateVersion7();
        var order = NewOrder(
            lines: [new OrderLineInput(frozen, frozen, 1m, 1_000m, 0m, 19, new QuotationProductSnapshot("A-001", "Tornillo"))]);
        order.AddItem(OrderItemId.New(), added, added, 1m, 1_000m, 0m, 19, OrderLineCosts.None, ConvertedBy, Now.AddHours(1));
        var version = order.Version;

        order.CaptureProductSnapshots(new Dictionary<Guid, QuotationProductSnapshot>
        {
            [frozen] = new("A-999", "Otro"),
            [added] = new("B-002", "Tuerca"),
        });

        Assert.Equal("A-001", order.Items.Single(item => item.ProductId == frozen).ProductCode);
        Assert.Equal("B-002", order.Items.Single(item => item.ProductId == added).ProductCode);
        // No es una edición: la versión ya la subió la línea que se agregó.
        Assert.Equal(version, order.Version);
    }

    // Spec de Shopify §6.3 (vía spec 2026-10-15 §5.4): el estado de pago que avisa la plataforma manda
    // sólo mientras el pedido no tenga comprobantes propios.
    [Fact]
    public void SyncExternalPaymentStatusChangesAnOrderWithoutProofs()
    {
        var order = NewOrder(proofs: []);
        var version = order.Version;

        order.SyncExternalPaymentStatus(OrderPaymentStatus.FullPaymentReceived, autoArchive: false, order.WithoutCosts(), Now.AddHours(1));

        Assert.Equal(OrderPaymentStatus.FullPaymentReceived, order.PaymentStatus);
        Assert.Equal(version + 1, order.Version);
    }

    [Fact]
    public void SyncExternalPaymentStatusIsRejectedWhenTheOrderHasProofs()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SyncExternalPaymentStatus(OrderPaymentStatus.PaymentPending, autoArchive: false, order.WithoutCosts(), Now.AddHours(1)));

        Assert.Equal("order.order.payment_has_proofs", error.Code);
        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
    }

    [Fact]
    public void SyncExternalPaymentStatusWithTheSameStatusIsNotAnEdit()
    {
        var order = NewOrder(proofs: []);
        var version = order.Version;

        order.SyncExternalPaymentStatus(OrderPaymentStatus.PaymentPending, autoArchive: false, order.WithoutCosts(), Now.AddHours(1));

        Assert.Equal(version, order.Version);
    }

    [Fact]
    public void FromQuotationCopiesHeaderPartiesAndLinesAndMatchesItsTotals()
    {
        var quotation = ConvertedQuotation();

        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", quotation, "Pago verificado",
            ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal((OrderSource.Quotation, (QuotationId?)quotation.Id), (order.Source, order.QuotationId));
        Assert.Equal((quotation.Subtotal, quotation.TaxAmount, quotation.Total, quotation.RetentionAmount, quotation.NetTotal),
            (order.Subtotal, order.TaxAmount, order.Total, order.RetentionAmount, order.NetTotal));
        Assert.Equal("Pago verificado", order.Notes);
        var item = Assert.Single(order.Items);
        Assert.Equal(("ABC", 10m, 5m), (item.ProductCode, item.Quantity, item.DiscountPercentage));
        Assert.Equal("Bodega", order.Shipping!.Name);
        Assert.NotEmpty(order.AppliedWithholdings);
    }

    [Fact]
    public void FromQuotationCopiesTheShippingPostalCode()
    {
        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", ConvertedQuotation(), null,
            ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal("050021", order.Shipping!.PostalCode);
    }

    [Fact]
    public void FromQuotationInheritsTheQuotationNotesWhenTheRequestOmitsThem()
    {
        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", ConvertedQuotation(), null,
            ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal("Entregar en portería", order.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FromQuotationWithEmptyNotesLeavesTheOrderWithoutNotes(string notes)
    {
        var order = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", ConvertedQuotation(), notes,
            ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Null(order.Notes);
    }

    private static Quotation ConvertedQuotation()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, Advisor, new DateOnly(2026, 11, 1), "Transferencia",
            "Entregar en portería",
            new QuotationParties(null, new QuotationPartyDetails
            {
                Name = "Bodega",
                Phone = "3001234567",
                Email = "bodega@x.co",
                Address = "Cl 1",
                DepartmentId = Guid.CreateVersion7(),
                CityId = Guid.CreateVersion7(),
                PostalCode = "050021",
            }),
            Account, "COP", customerVatSurplus: false, customerWithholdings: [TestWithholdings.RetefuenteCo], MunicipalContext.None, Advisor, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, 11_900m, 5m, 19, Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [quotation.Items.Single().VariantId] = new("ABC", "Aceite"),
        });

        return quotation;
    }

    [Fact]
    public void CreateStartsOpenWithItsProofs()
    {
        var fileId = Guid.CreateVersion7();
        var order = NewOrder(proofs: [new OrderPaymentProofInput(fileId, 50_000m)]);

        // Nace abierto y sin preparar: quien lo registra no es quien marca el pago.
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(OrderFulfillmentStatus.NotPrepared, order.FulfillmentStatus);
        Assert.Null(order.PaidMarkedAt);
        Assert.Null(order.PaidMarkedBy);
        Assert.Null(order.QuotationId);
        Assert.Equal(ConvertedBy, order.ConvertedBy);
        Assert.Equal(Now, order.ConvertedAt);
        Assert.Null(order.RitualCollectionSyncId);
        Assert.Equal(1, order.Version);
        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(fileId, proof.FileId);
        Assert.Equal(50_000m, proof.Amount);
        Assert.Equal(ConvertedBy, proof.UploadedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateRejectsBlankOrderNumber(string number)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            Order.CreateManual(
                OrderId.New(), TenantId, number, Header(), QuotationParties.Empty, Account, ShippingDetails.None, [Line()],
                null, ConvertedBy,
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 1m)], Now,
                orderDate: DateOnly.FromDateTime(Now.UtcDateTime)));

        Assert.Equal("order.order.number_required", error.Code);
    }

    [Fact]
    public void CreateRejectsAProofWithoutAFile()
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder(proofs: [new OrderPaymentProofInput(Guid.Empty, 100m)]));

        Assert.Equal("order.payment_proof.file_required", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateRejectsAProofWithANonPositiveAmount(decimal amount)
    {
        var error = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), amount)]));

        Assert.Equal("order.payment_proof.amount_invalid", error.Code);
    }

    [Fact]
    public void CreateAcceptsSeveralProofsWithDifferentAmounts()
    {
        var order = NewOrder(proofs:
        [
            new OrderPaymentProofInput(Guid.CreateVersion7(), 30_000m),
            new OrderPaymentProofInput(Guid.CreateVersion7(), 20_000m)
        ]);

        Assert.Equal(2, order.PaymentProofs.Count);
    }

    // A pedido (2026-09): cargar lo que faltaba sin recrear el pedido. Desde spec 2026-11-10 §3.2 el pago
    // sale de los comprobantes: cubrir el neto deja «Por verificar», nunca pagado.
    [Fact]
    public void AddPaymentProofsSumsProofsAndUpdatesThePaymentStatus()
    {
        var order = NewOrder(proofs: []);
        var newFileId = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(newFileId, 80_000m)],
            "Pago completado por transferencia",
            ConvertedBy,
            later);

        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Equal("Pago completado por transferencia", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // La pantalla precarga la nota con lo que ya había: mandar null (nadie tocó el campo) no
    // tiene por qué borrarla, pero tampoco hay forma de que este método lo sepa -- reemplaza
    // entero, como el resto de sus datos. Se deja explícito para que un cambio futuro no lo
    // convierta sin querer en "conservar si es null".
    [Fact]
    public void AddPaymentProofsReplacesTheNotesEntirelyEvenToNull()
    {
        var order = NewOrder(
            notes: "Nota original",
            proofs: []);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)],
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Null(order.Notes);
    }

    [Fact]
    public void AddPaymentProofsKeepsProofsAlreadyThere()
    {
        var existingFileId = Guid.CreateVersion7();
        var order = NewOrder(proofs: [new OrderPaymentProofInput(existingFileId, 50_000m)]);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)],
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Equal(2, order.PaymentProofs.Count);
        Assert.Contains(order.PaymentProofs, proof => proof.FileId == existingFileId);
    }

    [Fact]
    public void AddPaymentProofsRejectsAnEmptyBatch()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [], null, ConvertedBy, Now));

        Assert.Equal("order.order.payment_proof_required", error.Code);
    }

    // Preparado, el pedido ya se despachó con lo que tenía (spec 2026-11-10 §3.6): sumarle
    // comprobantes ahí adentro cambiaría lo que se dio por bueno.
    [Fact]
    public void AddPaymentProofsRejectsAPreparedOrder()
    {
        var order = NewOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                null,
                ConvertedBy,
                Now.AddDays(1)));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    // A pedido (2026-09): corregir un monto mal tipeado sin recrear el pedido ni el
    // comprobante -- el archivo y quien lo subio no cambian.
    [Fact]
    public void AddPaymentProofsCorrectsAnExistingProofAmount()
    {
        var existingFileId = Guid.CreateVersion7();
        var order = NewOrder(proofs: [new OrderPaymentProofInput(existingFileId, 10_000m)]);
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var later = Now.AddDays(1);

        order.AddPaymentProofs(
            [],
            null,
            ConvertedBy,
            later,
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m)]);

        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(proofId, proof.Id);
        Assert.Equal(existingFileId, proof.FileId);
        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(ConvertedBy, proof.UploadedBy);
        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Equal(2, order.Version);
    }

    // Sumar y corregir en el mismo llamado: un solo viaje de red para las dos cosas.
    [Fact]
    public void AddPaymentProofsAddsNewOnesAndCorrectsExistingOnesTogether()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);
        var existingProofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = Guid.CreateVersion7();

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(newFileId, 20_000m)],
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(existingProofId, 60_000m)]);

        Assert.Equal(2, order.PaymentProofs.Count);
        Assert.Contains(
            order.PaymentProofs, proof => proof.Id == existingProofId && proof.Amount == 60_000m);
        Assert.Contains(
            order.PaymentProofs, proof => proof.FileId == newFileId && proof.Amount == 20_000m);
    }

    [Fact]
    public void AddPaymentProofsRejectsCorrectingAProofThatIsNotOnThisOrder()
    {
        var order = NewOrder();
        var unknownProofId = OrderPaymentProofId.New();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [],
                null,
                ConvertedBy,
                Now.AddDays(1),
                [new OrderPaymentProofAmountUpdate(unknownProofId, 10_000m)]));

        Assert.Equal("order.payment_proof.not_found", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddPaymentProofsRejectsCorrectingAProofToANonPositiveAmount(decimal amount)
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [],
                null,
                ConvertedBy,
                Now.AddDays(1),
                [new OrderPaymentProofAmountUpdate(proofId, amount)]));

        Assert.Equal("order.payment_proof.amount_invalid", error.Code);
    }

    // Correcciones solas, sin sumar ningun comprobante nuevo, siguen siendo "algo para hacer" --
    // el chequeo de lote vacio no debe pedir tambien un comprobante nuevo.
    [Fact]
    public void AddPaymentProofsAllowsOnlyCorrectingWithoutAddingAnyNewProof()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;

        order.AddPaymentProofs(
            [],
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 5_000m)]);

        Assert.Equal(5_000m, Assert.Single(order.PaymentProofs).Amount);
    }

    // Preparado, el pedido está congelado (spec 2026-11-10 §3.6): corregir los montos ahí adentro
    // cambiaría lo que se dio por bueno.
    [Fact]
    public void AddPaymentProofsRejectsCorrectingAProofOnAPreparedOrder()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [],
                null,
                ConvertedBy,
                Now.AddDays(1),
                [new OrderPaymentProofAmountUpdate(proofId, 5_000m)]));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    // A pedido (2026-09-15): reemplazar el archivo de un comprobante mal cargado, junto con la
    // corrección de monto que ya existía.
    [Fact]
    public void AddPaymentProofsReplacesTheFileOfAnExistingProofWhenGiven()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);
        var proofId = Assert.Single(order.PaymentProofs).Id;
        var newFileId = Guid.CreateVersion7();

        order.AddPaymentProofs(
            [],
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m, newFileId)]);

        var proof = Assert.Single(order.PaymentProofs);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal(80_000m, proof.Amount);
    }

    [Fact]
    public void AddPaymentProofsKeepsTheFileWhenNoNewFileIdIsGiven()
    {
        var existingFileId = Guid.CreateVersion7();
        var order = NewOrder(proofs: [new OrderPaymentProofInput(existingFileId, 50_000m)]);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        order.AddPaymentProofs(
            [],
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m)]);

        Assert.Equal(existingFileId, Assert.Single(order.PaymentProofs).FileId);
    }

    // A pedido (2026-09-15): quitar un comprobante cargado por error, distinto de corregirlo.
    [Fact]
    public void RemovePaymentProofRemovesAnExistingProof()
    {
        var order = NewOrder(proofs:
        [
            new OrderPaymentProofInput(Guid.CreateVersion7(), 30_000m),
            new OrderPaymentProofInput(Guid.CreateVersion7(), 20_000m),
        ]);
        var proofId = order.PaymentProofs.First().Id;
        var later = Now.AddDays(1);

        order.RemovePaymentProof(proofId, later);

        Assert.Single(order.PaymentProofs);
        Assert.DoesNotContain(order.PaymentProofs, proof => proof.Id == proofId);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void RemovePaymentProofRejectsAnUnknownProof()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(OrderPaymentProofId.New(), Now.AddDays(1)));

        Assert.Equal("order.payment_proof.not_found", error.Code);
    }

    // Preparado, el pedido está congelado con los comprobantes que tenía en ese momento
    // (spec 2026-11-10 §3.6).
    [Fact]
    public void RemovePaymentProofRejectsAPreparedOrder()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(proofId, Now.AddDays(1)));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    // A pedido (2026-09): agregar un producto desde "Editar" pedido sube el total de la
    // cotización; lo cargado en comprobantes no cambia, pero el estado de pago sí puede.
    // El neto del pedido por defecto es 23.800 (ver NewOrder): el monto adeudado sale del pedido,
    // no de un parámetro.
    [Theory]
    [InlineData(10_000, OrderPaymentStatus.PartialPaymentReceived)]
    [InlineData(23_800, OrderPaymentStatus.PendingVerification)]
    [InlineData(30_000, OrderPaymentStatus.PendingVerification)]
    public void RecalculatePaymentStatusComparesProofsAgainstTheOrdersNetTotal(
        decimal proofAmount, OrderPaymentStatus expected)
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), proofAmount)]);
        var later = Now.AddDays(1);

        order.RecalculatePaymentStatus(order.NetTotal, later);

        Assert.Equal(expected, order.PaymentStatus);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // Hotfix 2026-10-19: la retención (2,5 % redondeado a centavos) deja el neto con fracción de
    // peso. La pantalla lo muestra en pesos enteros y la persona carga eso; comparar contra los
    // centavos lo dejaba en pago parcial. Un peso de menos sigue siendo parcial.
    [Theory]
    [InlineData(116_529, OrderPaymentStatus.PendingVerification)]
    [InlineData(116_528, OrderPaymentStatus.PartialPaymentReceived)]
    public void RecalculatePaymentStatusComparesAgainstTheNetAsTheCurrencyDisplaysIt(
        decimal proofAmount, OrderPaymentStatus expected)
    {
        var order = NewOrder(
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), proofAmount)],
            lines: [Line(quantity: 1m, unitPrice: 119_030m)],
            header: Header(withRetention: true));
        Assert.Equal(116_529.37m, order.NetTotal);

        order.RecalculatePaymentStatus(order.NetTotal, Now.AddDays(1));

        Assert.Equal(expected, order.PaymentStatus);
    }

    [Fact]
    public void RecalculatePaymentStatusIsPendingWithoutAnyProof()
    {
        var order = NewOrder(proofs: []);

        order.RecalculatePaymentStatus(order.NetTotal, Now.AddDays(1));

        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
    }

    // Preparado, el pedido está congelado con el total que tenía en ese momento — este método no se
    // llama en ese caso (el caso de uso que agrega el
    // producto ya lo bloquea antes), pero el agregado se defiende igual.
    [Fact]
    public void RecalculatePaymentStatusRejectsAPreparedOrder()
    {
        var order = NewOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.RecalculatePaymentStatus(order.NetTotal, Now.AddDays(1)));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    // Spec 2026-09-15, P5: cada comprobante guarda la clave de su copia pública, que le llega ya
    // armada desde el handler.
    [Fact]
    public void CreateKeepsThePublicStorageKeyOfEachProof()
    {
        var order = NewOrder(proofs:
        [
            new OrderPaymentProofInput(Guid.CreateVersion7(), 60_000m, "payment-proofs/a.pdf"),
            new OrderPaymentProofInput(Guid.CreateVersion7(), 40_000m, "payment-proofs/b.png"),
        ]);

        Assert.Equal(
            ["payment-proofs/a.pdf", "payment-proofs/b.png"],
            order.PaymentProofs.Select(proof => proof.PublicStorageKey));
    }

    // P8: con la opción apagada, y en los comprobantes de antes, no hay copia pública.
    [Fact]
    public void CreateLeavesThePublicStorageKeyNullWhenThereIsNone()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);

        Assert.Null(Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    [Fact]
    public void AddPaymentProofsKeepsThePublicStorageKeyOfTheNewProofs()
    {
        var order = NewOrder(proofs: []);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m, "payment-proofs/c.jpg")],
            null,
            ConvertedBy,
            Now.AddDays(1));

        Assert.Equal("payment-proofs/c.jpg", Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    // P4: corregir el monto no toca el archivo, así que tampoco su copia pública.
    [Fact]
    public void CorrectingAnAmountKeepsThePublicStorageKey()
    {
        var order = NewOrder(
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m, "payment-proofs/d.pdf")]);
        var proofId = Assert.Single(order.PaymentProofs).Id;

        order.AddPaymentProofs(
            [],
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(proofId, 80_000m)]);

        Assert.Equal("payment-proofs/d.pdf", Assert.Single(order.PaymentProofs).PublicStorageKey);
    }

    // Spec 2026-09-16 (anular un pedido), decisiones 1 y 2: se anula un abierto, y queda
    // quién, cuándo y por qué.
    [Fact]
    public void CancelFromOpenRecordsWhoWhenAndWhy()
    {
        var order = NewOrder();
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancelledBy);
        Assert.Null(order.CancellationReason);
        var cancelledBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.Cancel(cancelledBy, "El cliente desistió", later);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(later, order.CancelledAt);
        Assert.Equal(cancelledBy, order.CancelledBy);
        Assert.Equal("El cliente desistió", order.CancellationReason);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // Decisiones 1 y 2: un pedido marcado pagado por error también se anula, y anularlo no reescribe
    // quién lo marcó ni cuándo.
    [Fact]
    public void CancelFromPaidKeepsThePaidMark()
    {
        var order = NewOrder();
        var paidBy = new MemberId(Guid.CreateVersion7());
        order.MarkPaid(paidBy, requirePaymentProof: false, order.WithoutCosts(), Now);

        order.Cancel(ConvertedBy, "Pagado por error", Now.AddDays(1));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(paidBy, order.PaidMarkedBy);
        Assert.Equal(Now, order.PaidMarkedAt);
        Assert.Equal(3, order.Version);
    }

    // Mandarle al cliente un pedido anulado se lee como vigente: es lo único que frena el envío.
    [Fact]
    public void ACancelledOrderCannotBeSent()
    {
        var order = NewOrder();
        order.Cancel(ConvertedBy, "El cliente desistió", Now);

        var error = Assert.Throws<QuotationsDomainException>(order.EnsureSendable);

        Assert.Equal("order.order.already_cancelled", error.Code);
    }

    [Fact]
    public void OpenPaidAndInvoicedOrdersCanBeSent()
    {
        var order = NewOrder();
        order.EnsureSendable();
        order.MarkPaid(ConvertedBy, requirePaymentProof: false, order.WithoutCosts(), Now);
        order.EnsureSendable();
        order.Invoice(ConvertedBy, requireFullPayment: true, Now);

        order.EnsureSendable();

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.True(order.IsInvoiced);
    }

    // Anular dos veces reescribiría quién, cuándo y por qué se anuló.
    [Fact]
    public void CancelTwiceIsRejectedAndKeepsTheFirstCancellation()
    {
        var order = NewOrder();
        order.Cancel(ConvertedBy, "Primera vez", Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(new MemberId(Guid.CreateVersion7()), "Segunda vez", Now.AddDays(1)));

        Assert.Equal("order.order.already_cancelled", error.Code);
        Assert.Equal("Primera vez", order.CancellationReason);
        Assert.Equal(Now, order.CancelledAt);
        Assert.Equal(ConvertedBy, order.CancelledBy);
    }

    // Decisión 4: el motivo es obligatorio. Ya anulado gana already_cancelled (hallazgo 6).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CancelRequiresAReason(string? reason)
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, reason, Now));

        Assert.Equal("order.order.cancellation_reason_required", error.Code);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Null(order.CancelledAt);
    }

    [Fact]
    public void CancelRejectsAReasonLongerThanTheLimit()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, new string('a', Order.CancellationReasonMaxLength + 1), Now));

        Assert.Equal("order.order.cancellation_reason_too_long", error.Code);
        Assert.Equal(OrderStatus.Open, order.Status);
    }

    // El límite se mide sobre el motivo recortado, mismo criterio que las notas.
    [Fact]
    public void CancelTrimsTheReasonBeforeMeasuringIt()
    {
        var order = NewOrder();
        var reason = new string('a', Order.CancellationReasonMaxLength);

        order.Cancel(ConvertedBy, $"  {reason}  ", Now);

        Assert.Equal(reason, order.CancellationReason);
    }

    // Un anulado no es Open, así que no se edita (spec 2026-11-10 §3.6) ni se marca pagado.
    [Fact]
    public void ACancelledOrderRejectsEveryEditOnlyChange()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.Cancel(ConvertedBy, "El cliente desistió", Now);
        var later = Now.AddDays(1);

        var addProofs = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                null,
                ConvertedBy,
                later));
        var recalculate = Assert.Throws<QuotationsDomainException>(() =>
            order.RecalculatePaymentStatus(order.NetTotal, later));
        var removeProof = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(proofId, later));
        var markPaid = Assert.Throws<QuotationsDomainException>(() =>
            order.MarkPaid(ConvertedBy, requirePaymentProof: false, order.WithoutCosts(), later));

        Assert.Equal("order.order.not_editable", addProofs.Code);
        Assert.Equal("order.order.not_editable", recalculate.Code);
        Assert.Equal("order.order.not_editable", removeProof.Code);
        Assert.Equal("order.order.not_open", markPaid.Code);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    // Spec 2026-09-17 (editar pedido como borrador): el guardado atómico suma comprobantes sin
    // pisar el estado de pago —lo deriva el servidor una sola vez, decisión 5— ni las notas.
    [Fact]
    public void AttachPaymentProofsAddsTheProofsWithoutTouchingPaymentStatusOrNotes()
    {
        var order = NewOrder(notes: "Entregar el lunes", proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)]);
        var fileId = Guid.CreateVersion7();
        var uploadedBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.AttachPaymentProofs([new OrderPaymentProofInput(fileId, 30_000m, "payment-proofs/a.pdf")], uploadedBy, later);

        Assert.Equal(2, order.PaymentProofs.Count);
        var added = Assert.Single(order.PaymentProofs, proof => proof.FileId == fileId);
        Assert.Equal(30_000m, added.Amount);
        Assert.Equal(uploadedBy, added.UploadedBy);
        Assert.Equal("payment-proofs/a.pdf", added.PublicStorageKey);
        Assert.Equal(OrderPaymentStatus.PartialPaymentReceived, order.PaymentStatus);
        Assert.Equal("Entregar el lunes", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // A diferencia de AddPaymentProofs, un guardado que sólo toca productos no trae comprobantes:
    // no es un error y no sube la versión.
    [Fact]
    public void AttachPaymentProofsWithNothingToAttachChangesNothing()
    {
        var order = NewOrder();

        order.AttachPaymentProofs([], ConvertedBy, Now.AddDays(1));

        Assert.Single(order.PaymentProofs);
        Assert.Equal(Now, order.UpdatedAt);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void AttachPaymentProofsRejectsAnOrderThatIsNotEditable()
    {
        var order = NewOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachPaymentProofs([], ConvertedBy, Now.AddDays(1)));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    [Fact]
    public void CorrectPaymentProofsUpdatesTheAmountAndReplacesTheFile()
    {
        var order = NewOrder();
        var proof = Assert.Single(order.PaymentProofs);
        var newFileId = Guid.CreateVersion7();
        var later = Now.AddDays(1);

        order.CorrectPaymentProofs(
            [new OrderPaymentProofAmountUpdate(proof.Id, 80_000m, newFileId, "payment-proofs/b.pdf")],
            later);

        Assert.Equal(80_000m, proof.Amount);
        Assert.Equal(newFileId, proof.FileId);
        Assert.Equal("payment-proofs/b.pdf", proof.PublicStorageKey);
        // Corregir no recalcula: el pago queda como nació hasta que el guardado lo vuelva a mirar.
        Assert.Equal(OrderPaymentStatus.PendingVerification, order.PaymentStatus);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    // Todos los ids se buscan antes de corregir el primero: uno ajeno no deja la mitad corregida.
    [Fact]
    public void CorrectPaymentProofsRejectsAProofThatIsNotOnThisOrderBeforeCorrectingAny()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m)]);
        var proof = Assert.Single(order.PaymentProofs);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs(
                [
                    new OrderPaymentProofAmountUpdate(proof.Id, 1m),
                    new OrderPaymentProofAmountUpdate(OrderPaymentProofId.New(), 2m),
                ],
                Now.AddDays(1)));

        Assert.Equal("order.payment_proof.not_found", error.Code);
        Assert.Equal(100_000m, proof.Amount);
        Assert.Equal(1, order.Version);
    }

    [Fact]
    public void CorrectPaymentProofsWithNothingToCorrectChangesNothing()
    {
        var order = NewOrder();

        order.CorrectPaymentProofs([], Now.AddDays(1));

        Assert.Equal(1, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Fact]
    public void CorrectPaymentProofsRejectsAnOrderThatIsNotEditable()
    {
        var order = NewOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs([], Now.AddDays(1)));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    // Spec 2026-09-17: notes reemplaza el campo entero, y el guardado necesita saber si cambió para
    // responder «sin cambios» sin subir la versión.
    [Fact]
    public void UpdateNotesReplacesTheTrimmedNotesAndReportsTheChange()
    {
        var order = NewOrder(notes: "Entregar el lunes");
        var later = Now.AddDays(1);

        var changed = order.UpdateNotes("  Entregar el martes  ", later);

        Assert.True(changed);
        Assert.Equal("Entregar el martes", order.Notes);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void UpdateNotesWithTheSameNotesReportsNoChange()
    {
        var order = NewOrder(notes: "Entregar el lunes");

        var changed = order.UpdateNotes(" Entregar el lunes ", Now.AddDays(1));

        Assert.False(changed);
        Assert.Equal(1, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void UpdateNotesClearsThemWithNullOrBlank(string? notes)
    {
        var order = NewOrder(notes: "Entregar el lunes");

        var changed = order.UpdateNotes(notes, Now.AddDays(1));

        Assert.True(changed);
        Assert.Null(order.Notes);
    }

    // P2 del plan: las notas del pedido heredan las de la cotización, que no tienen tope.
    [Fact]
    public void UpdateNotesAcceptsLongNotes()
    {
        var order = NewOrder();

        Assert.True(order.UpdateNotes(new string('a', 2_000), Now.AddDays(1)));
        Assert.Equal(2_000, order.Notes!.Length);
    }

    [Fact]
    public void UpdateNotesRejectsAnOrderThatIsNotEditable()
    {
        var order = NewOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes("Otra nota", Now.AddDays(1)));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    // Spec 2026-11-16 §3: «Fecha del pedido» es un día del calendario del tenant, aparte de ConvertedAt, y se
    // edita con el patrón de UpdateNotes.
    private static readonly DateOnly Today = new(2026, 11, 16);

    [Fact]
    public void ANewOrderKeepsTheOrderDateItWasGiven()
    {
        var order = NewOrder(orderDate: new DateOnly(2026, 11, 13));

        Assert.Equal(new DateOnly(2026, 11, 13), order.OrderDate);
    }

    // Spec 2026-11-16 §7: cada fábrica deja la fecha que recibe; el dominio no la deduce de occurredAt.
    [Fact]
    public void EveryFactoryKeepsTheOrderDateItWasGiven()
    {
        var date = new DateOnly(2026, 11, 13);
        Order[] orders =
        [
            Order.FromQuotation(OrderId.New(), TenantId, "PED-2026-0001", ConvertedQuotation(), null, ConvertedBy, [], Now, date),
            Order.Preview(
                OrderId.New(), TenantId, "PREVIEW", Header(), QuotationParties.Empty, null, ShippingDetails.None, [Line()],
                null, ConvertedBy, [], Now, date),
            Order.CreateExternal(
                OrderId.New(), TenantId, "PED-2026-0002", Header(), QuotationParties.Empty, Account, [Line()],
                OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now, date, OrderSource.Shopify, "#1001",
                ShippingDetails.None, null, null, OrderLineCosts.None),
            Order.CreateFromStore(
                OrderId.New(), TenantId, "PED-2026-0003", Header(isRetail: true), StoreParties(), Account, [Line()], null,
                Advisor, Now, date, "store-checkout:abc"),
            Order.QuoteForStore(TenantId, "COP", isRetail: true, [Line()], Now, date),
        ];

        Assert.All(orders, order => Assert.Equal(date, order.OrderDate));
    }

    // Spec 2026-11-16 §3 (F-6): la columna del historial guarda el nombre, así que el miembro nuevo va al final y no reordena.
    // Mayorista fase 3 sumó WholesaleApproved después.
    [Fact]
    public void WholesaleApprovedIsTheLastHistoryEventType()
    {
        Assert.Equal("WholesaleApproved", Enum.GetNames<OrderHistoryEventType>()[^1]);
        Assert.Equal("OrderDateChanged", Enum.GetNames<OrderHistoryEventType>()[^2]);
        Assert.Equal("BuyerAccountChanged", Enum.GetNames<OrderHistoryEventType>()[^3]);
    }

    [Fact]
    public void ChangingTheOrderDateMovesItAndBumpsTheVersion()
    {
        var order = NewOrder(orderDate: new DateOnly(2026, 11, 13));
        var before = order.Version;
        var later = Now.AddDays(1);

        var changed = order.ChangeOrderDate(new DateOnly(2026, 11, 10), Today, later);

        Assert.True(changed);
        Assert.Equal(new DateOnly(2026, 11, 10), order.OrderDate);
        Assert.Equal(before + 1, order.Version);
        Assert.Equal(later, order.UpdatedAt);
    }

    [Fact]
    public void TheSameOrderDateIsNotAChange()
    {
        var order = NewOrder(orderDate: new DateOnly(2026, 11, 13));
        var before = order.Version;

        var changed = order.ChangeOrderDate(order.OrderDate, Today, Now.AddDays(1));

        Assert.False(changed);
        Assert.Equal(before, order.Version);
        Assert.Equal(Now, order.UpdatedAt);
    }

    // Un pedido cuya fecha guardada quedó después de «hoy» (otro huso, reloj de un pod) acepta que se le reenvíe la
    // misma fecha: el save-all manda siempre la del pedido y no puede fallar con in_future por algo que no cambió.
    [Fact]
    public void ResendingAStoredFutureOrderDateIsNotAChangeAndDoesNotFail()
    {
        var stored = Today.AddDays(1);
        var order = NewOrder(orderDate: stored);
        var before = order.Version;

        var changed = order.ChangeOrderDate(stored, Today, Now.AddDays(1));

        Assert.False(changed);
        Assert.Equal(stored, order.OrderDate);
        Assert.Equal(before, order.Version);
    }

    [Fact]
    public void TodayIsAnAcceptedOrderDate()
    {
        var order = NewOrder(orderDate: new DateOnly(2026, 11, 13));

        Assert.True(order.ChangeOrderDate(Today, Today, Now.AddDays(1)));
        Assert.Equal(Today, order.OrderDate);
    }

    [Fact]
    public void AFutureOrderDateIsRejected()
    {
        var order = NewOrder(orderDate: new DateOnly(2026, 11, 13));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.ChangeOrderDate(Today.AddDays(1), Today, Now.AddDays(1)));

        Assert.Equal("order.order_date.in_future", error.Code);
        Assert.Equal(new DateOnly(2026, 11, 13), order.OrderDate);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("invoiced")]
    [InlineData("cancelled")]
    public void ALockedOrderCannotChangeItsDate(string state)
    {
        var order = NewOrder(orderDate: new DateOnly(2026, 11, 13));
        switch (state)
        {
            case "prepared":
                order.Prepare(ConvertedBy, Now);
                break;
            case "invoiced":
                order.Invoice(ConvertedBy, requireFullPayment: false, Now);
                break;
            case "cancelled":
                order.Cancel(ConvertedBy, "El cliente desistió", Now);
                break;
        }

        // El mismo rechazo que UpdateNotes da en ese estado: el pedido bloqueado no se edita por ninguna vía.
        var notes = Assert.Throws<QuotationsDomainException>(() => order.UpdateNotes("Otra nota", Now.AddDays(1)));
        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.ChangeOrderDate(new DateOnly(2026, 11, 10), Today, Now.AddDays(1)));

        Assert.Equal(notes.Code, error.Code);
        Assert.Equal("order.order.not_editable", error.Code);
        Assert.Equal(new DateOnly(2026, 11, 13), order.OrderDate);
    }

    // Spec 2026-10-26 §4.4 y §4.6: la parte de envío de un pedido externo y su ciudad corregible.
    private static readonly Guid DepartmentA = Guid.CreateVersion7();
    private static readonly Guid CityA = Guid.CreateVersion7();
    private static readonly Guid CityB = Guid.CreateVersion7();

    private static QuotationPartyDetails ShippingParty(Guid? cityId = null, string? email = "ana@correo.co") => new()
    {
        Name = "Ana Pérez",
        Phone = "3001112233",
        Email = email,
        Address = "Cra 5 # 1-2",
        DepartmentId = DepartmentA,
        CityId = cityId ?? CityA,
    };

    private const string RawAddressPrefix = "Dirección de envío de Shopify:";

    private static Order NewShopifyOrder(string? notes = null, QuotationParties? parties = null) =>
        Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0003", Header(), parties ?? QuotationParties.Empty, Account, [Line()],
            OrderPaymentStatus.PaymentPending, notes, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Shopify, "#1001",
            ShippingDetails.None, null, null, OrderLineCosts.None);

    // Spec 2026-11-14: la nota del comprador de Shopify va aparte de las notas del asesor, como la de la tienda pública.
    [Fact]
    public void CreateExternalKeepsTheBuyerNotesApartFromNotes()
    {
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0003", Header(), QuotationParties.Empty, Account, [Line()],
            OrderPaymentStatus.PaymentPending, "Shopify #1001", ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Shopify, "#1001",
            ShippingDetails.None, null, null, OrderLineCosts.None, buyerNotes: "  Dejar en portería ");

        Assert.Equal(("Shopify #1001", "Dejar en portería"), (order.Notes, order.BuyerNotes));
    }

    [Fact]
    public void UpdateExternalBuyerNotesChangesOnlyBuyerNotesAndBumpsVersion()
    {
        var order = NewShopifyOrder("Shopify #1001");
        var later = Now.AddHours(1);

        var changed = order.UpdateExternalBuyerNotes("  Sin cebolla ", later);

        Assert.True(changed);
        Assert.Equal(("Sin cebolla", "Shopify #1001"), (order.BuyerNotes, order.Notes));
        Assert.Equal((later, 2), (order.UpdatedAt, order.Version));
    }

    [Fact]
    public void UpdateExternalBuyerNotesWithTheSameTextChangesNothing()
    {
        var order = NewShopifyOrder("Shopify #1001");
        order.UpdateExternalBuyerNotes("Sin cebolla", Now.AddHours(1));

        var changed = order.UpdateExternalBuyerNotes(" Sin cebolla  ", Now.AddHours(2));

        Assert.False(changed);
        Assert.Equal((Now.AddHours(1), 2), (order.UpdatedAt, order.Version));
    }

    // D22: vacío o sólo espacios es lo mismo que null.
    [Fact]
    public void UpdateExternalBuyerNotesWithBlankOnANullNoteChangesNothing()
    {
        var order = NewShopifyOrder("Shopify #1001");

        Assert.False(order.UpdateExternalBuyerNotes("   ", Now.AddHours(1)));
        Assert.Null(order.BuyerNotes);
        Assert.Equal(1, order.Version);
    }

    // La nota del comprador de la tienda pública es lo que él escribió: el asesor no la cambia.
    [Fact]
    public void UpdateExternalBuyerNotesOnAStoreOrderIsSourceInvalid()
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7(), notes: "Entregar en portería");

        var error = Assert.Throws<QuotationsDomainException>(() => order.UpdateExternalBuyerNotes("Otra", Now.AddHours(1)));

        Assert.Equal("order.order.source_invalid", error.Code);
        Assert.Equal("Entregar en portería", order.BuyerNotes);
    }

    [Fact]
    public void UpdateExternalBuyerNotesOnAPreparedOrderIsNotEditable()
    {
        var order = NewShopifyOrder("Shopify #1001");
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() => order.UpdateExternalBuyerNotes("Otra", Now.AddHours(1)));

        Assert.Equal("order.order.not_editable", error.Code);
    }

    [Fact]
    public void AttachExternalShippingAddsThePartyAndDropsTheRawAddressLine()
    {
        var order = NewShopifyOrder("Shopify #1001\nNota\nDirección de envío de Shopify: Cra 5, CALLI");
        var later = Now.AddHours(1);

        var changed = order.AttachExternalShipping(ShippingParty(), RawAddressPrefix, [CityA], later);

        Assert.True(changed);
        Assert.Equal("Shopify #1001\nNota", order.Notes);
        Assert.NotNull(order.Shipping);
        Assert.Equal(ShippingParty(), order.Shipping!.ToDetails());
        Assert.Equal((later, 2), (order.UpdatedAt, order.Version));
    }

    [Fact]
    public void AttachExternalShippingWithTheSamePartyIsANoOp()
    {
        var order = NewShopifyOrder();
        order.AttachExternalShipping(ShippingParty(), RawAddressPrefix, [CityA], Now.AddHours(1));

        var changed = order.AttachExternalShipping(ShippingParty(), RawAddressPrefix, [CityA], Now.AddHours(2));

        Assert.False(changed);
        Assert.Equal(2, order.Version);
    }

    [Fact]
    public void AttachExternalShippingOverAnotherPartyIsShippingPartyPresent()
    {
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty() with { Phone = "3009998877" }));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachExternalShipping(ShippingParty(), RawAddressPrefix, [CityA], Now.AddHours(1)));

        Assert.Equal("order.order.shipping_party_present", error.Code);
    }

    // Spec 2026-10-26 §4.4 y desvío S1: la misma parte en otra ciudad que el trabajo pidió la puso un intento anterior, con la
    // ciudad que el sinónimo tenía entonces. Es la misma escritura: no la toca, y el consumidor guarda la ciudad que quedó (y la
    // recorrige si el sinónimo ya apunta a otra).
    [Fact]
    public void AttachExternalShippingOverOurPartyInARequestedCityIsANoOp()
    {
        var order = NewShopifyOrder();
        order.AttachExternalShipping(ShippingParty(CityB), RawAddressPrefix, [CityB], Now.AddHours(1));

        var changed = order.AttachExternalShipping(ShippingParty(), RawAddressPrefix, [CityB, CityA], Now.AddHours(2));

        Assert.False(changed);
        Assert.Equal(2, order.Version);
        Assert.Equal(ShippingParty(CityB), order.Shipping!.ToDetails());
    }

    // Una persona que sólo cambió la ciudad la llevó a una que el trabajo no pidió: no es un reintento, gana la persona.
    [Fact]
    public void AttachExternalShippingOverOurPartyInACityNobodyRequestedIsShippingPartyPresent()
    {
        var chosen = Guid.CreateVersion7();
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty(chosen)));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachExternalShipping(ShippingParty(), RawAddressPrefix, [CityA], Now.AddHours(1)));

        Assert.Equal("order.order.shipping_party_present", error.Code);
        Assert.Equal((ShippingParty(chosen), 1), (order.Shipping!.ToDetails(), order.Version));
    }

    // Review Focus 3: sólo se agrega la parte mientras el pedido se puede editar.
    [Fact]
    public void AttachExternalShippingOnAPreparedOrderIsNotEditable()
    {
        var order = NewShopifyOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachExternalShipping(ShippingParty(), RawAddressPrefix, [CityA], Now.AddHours(1)));

        Assert.Equal("order.order.not_editable", error.Code);
        Assert.Null(order.Shipping);
    }

    [Fact]
    public void AttachExternalShippingNeedsACompleteParty()
    {
        var order = NewShopifyOrder("Nota");

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachExternalShipping(ShippingParty(email: null), RawAddressPrefix, [CityA], Now.AddHours(1)));

        Assert.Equal("order.shipping.party_incomplete", error.Code);
        Assert.Null(order.Shipping);
        Assert.Equal(1, order.Version);
    }

    // Spec 2026-11-14 §5: el asesor corrige la parte de envío de un pedido de Shopify. Si no la tenía, se crea y la línea cruda
    // de la dirección sale de las notas.
    [Fact]
    public void EditExternalShippingPartyCreatesItAndDropsTheRawAddressLine()
    {
        var order = NewShopifyOrder("Shopify #1001\nDirección de envío de Shopify: Cra 5, CALLI");
        var later = Now.AddHours(1);

        var changed = order.EditExternalShippingParty(ShippingParty(), RawAddressPrefix, later);

        Assert.True(changed);
        Assert.Equal("Shopify #1001", order.Notes);
        Assert.Equal(ShippingParty(), order.Shipping!.ToDetails());
        Assert.Equal((later, 2), (order.UpdatedAt, order.Version));
    }

    [Fact]
    public void EditExternalShippingPartyReplacesTheExistingOne()
    {
        var order = NewShopifyOrder("Shopify #1001", parties: new QuotationParties(null, ShippingParty()));
        var corrected = ShippingParty(CityB) with { Address = "Calle 9 # 8-7" };

        var changed = order.EditExternalShippingParty(corrected, RawAddressPrefix, Now.AddHours(1));

        Assert.True(changed);
        Assert.Equal(corrected, order.Shipping!.ToDetails());
        Assert.Equal(2, order.Version);
        Assert.False(order.EditExternalShippingParty(corrected, RawAddressPrefix, Now.AddHours(2)));
        Assert.Equal(2, order.Version);
    }

    // La parte de un pedido de la tienda pública es la que el comprador escribió: el asesor no la corrige por este camino.
    [Fact]
    public void EditExternalShippingPartyOnAStoreOrderIsSourceInvalid()
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7());
        var before = order.Shipping?.ToDetails();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.EditExternalShippingParty(ShippingParty(CityB), RawAddressPrefix, Now.AddHours(1)));

        Assert.Equal("order.order.source_invalid", error.Code);
        Assert.Equal(before, order.Shipping?.ToDetails());
    }

    // Spec 2026-11-14 §4: una edición de Shopify que se aplica al pedido. Las líneas llegan preciadas por la plataforma.
    private static ExternalOrderEdit Edit(
        IReadOnlyCollection<OrderLineInput>? lines = null,
        ShippingDetails? shipping = null,
        QuotationPartyDetails? party = null,
        bool replaceBuyerNotes = false,
        string? buyerNotes = null) =>
        new(lines, shipping, party, replaceBuyerNotes, buyerNotes, RawAddressPrefix);

    private static OrderLineInput SameLine(OrderItem item, decimal? quantity = null) =>
        new(item.VariantId, item.ProductId, quantity ?? item.Quantity, item.UnitPrice, item.DiscountPercentage, item.TaxPercentage);

    [Fact]
    public void ApplyExternalEditReplacesTheLinesAndBumpsVersionOnce()
    {
        var first = Line();
        var second = Line(quantity: 1m, unitPrice: 23_800m);
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0003", Header(), QuotationParties.Empty, Account, [first, second],
            OrderPaymentStatus.PaymentPending, "Shopify #1001", ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Shopify, "#1001",
            ShippingDetails.None, null, null, OrderLineCosts.None);
        var later = Now.AddHours(1);

        var result = order.ApplyExternalEdit(
            Edit(lines: [first with { Quantity = 1m }]), OrderLineCosts.None, later);

        Assert.Equal(new ExternalOrderEditResult(true, false, false, false), result);
        var item = Assert.Single(order.Items);
        Assert.Equal((first.VariantId, 1m), (item.VariantId, item.Quantity));
        Assert.Equal(11_900m, order.Total);
        Assert.Equal((later, 2), (order.UpdatedAt, order.Version));
    }

    [Fact]
    public void ApplyExternalEditWithTheSameValuesChangesNothing()
    {
        var order = NewShopifyOrder("Shopify #1001", parties: new QuotationParties(null, ShippingParty()));
        order.UpdateExternalBuyerNotes("Sin cebolla", Now);
        var version = order.Version;

        var result = order.ApplyExternalEdit(
            Edit(
                lines: [SameLine(order.Items.Single())], shipping: ShippingDetails.None, party: ShippingParty(),
                replaceBuyerNotes: true, buyerNotes: " Sin cebolla "),
            OrderLineCosts.None,
            Now.AddHours(1));

        Assert.False(result.Any);
        Assert.Equal((version, Now), (order.Version, order.UpdatedAt));
    }

    [Fact]
    public void ApplyExternalEditCreatesThePartyAndDropsTheRawAddressLine()
    {
        var order = NewShopifyOrder("Shopify #1001\nDirección de envío de Shopify: Calle 1");

        var result = order.ApplyExternalEdit(Edit(party: ShippingParty()), OrderLineCosts.None, Now.AddHours(1));

        Assert.Equal(new ExternalOrderEditResult(false, false, true, false), result);
        Assert.Equal("Shopify #1001", order.Notes);
        Assert.NotNull(order.Shipping);
        Assert.True(order.Shipping!.IsComplete);
        Assert.Equal(ShippingParty(), order.Shipping.ToDetails());
        Assert.Equal(2, order.Version);
    }

    // Spec 2026-11-14 §4: los precios son los de la plataforma, sin repreciar contra el catálogo.
    [Fact]
    public void ApplyExternalEditKeepsThePlatformUnitPrice()
    {
        var usd = new OrderHeader(ClientId, Advisor, "USD", null, false, null, false, []);
        var order = Order.CreateExternal(
            OrderId.New(), TenantId, "PED-2026-0003", usd, QuotationParties.Empty, Account with { Currency = "USD" },
            [Line(unitPrice: 30m)], OrderPaymentStatus.PaymentPending, null, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), OrderSource.Shopify,
            "#1001", ShippingDetails.None, null, null, OrderLineCosts.None);
        var variant = order.Items.Single();

        order.ApplyExternalEdit(
            Edit(lines: [new OrderLineInput(variant.VariantId, variant.ProductId, 1m, 47_619.05m, 10m, 0)]),
            OrderLineCosts.None,
            Now.AddHours(1));

        var item = Assert.Single(order.Items);
        Assert.Equal((47_619.05m, 10m), (item.UnitPrice, item.DiscountPercentage));
    }

    [Fact]
    public void ApplyExternalEditOnAPreparedOrderIsNotEditable()
    {
        var order = NewShopifyOrder();
        order.Prepare(ConvertedBy, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.ApplyExternalEdit(Edit(replaceBuyerNotes: true, buyerNotes: "Otra"), OrderLineCosts.None, Now.AddHours(1)));

        Assert.Equal("order.order.not_editable", error.Code);
        Assert.Null(order.BuyerNotes);
    }

    [Fact]
    public void ApplyExternalEditOnAManualOrderIsSourceInvalid()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.ApplyExternalEdit(Edit(replaceBuyerNotes: true, buyerNotes: "Otra"), OrderLineCosts.None, Now.AddHours(1)));

        Assert.Equal("order.order.source_invalid", error.Code);
    }

    [Fact]
    public void ApplyExternalEditWithAPartyOnAPickupOrderIsStorePickup()
    {
        var order = NewShopifyOrder(parties: new QuotationParties(null, null, IsStorePickup: true));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.ApplyExternalEdit(Edit(party: ShippingParty()), OrderLineCosts.None, Now.AddHours(1)));

        Assert.Equal("order.order.store_pickup", error.Code);
        Assert.Null(order.Shipping);
    }

    [Fact]
    public void CorrectExternalShippingCityChangesOnlyCityAndDepartment()
    {
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty()));
        var newDepartment = Guid.CreateVersion7();
        var later = Now.AddHours(1);

        var changed = order.CorrectExternalShippingCity(ShippingParty(), newDepartment, CityB, [CityB], later);

        Assert.True(changed);
        Assert.Equal(ShippingParty(CityB) with { DepartmentId = newDepartment }, order.Shipping!.ToDetails());
        Assert.Equal((later, 2), (order.UpdatedAt, order.Version));
    }

    // Review Focus 4: si alguien tocó la parte, ya no es la que Shopify guardó y no se corrige.
    [Fact]
    public void CorrectExternalShippingCityWithAnEditedPartyIsShippingPartyChanged()
    {
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty() with { Phone = "3009998877" }));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectExternalShippingCity(ShippingParty(), DepartmentA, CityB, [CityB], Now.AddHours(1)));

        Assert.Equal("order.order.shipping_party_changed", error.Code);
    }

    [Fact]
    public void CorrectExternalShippingCityAlreadyAtTheTargetIsANoOp()
    {
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty()));

        var changed = order.CorrectExternalShippingCity(ShippingParty(), DepartmentA, CityA, [CityA], Now.AddHours(1));

        Assert.False(changed);
        Assert.Equal(1, order.Version);
    }

    // Spec 2026-10-26 §4.6: el trabajo que recorrige puede caer después de que Quotations guardó y antes de que Shopify
    // actualizara su huella. Al reintentar manda la huella vieja y encuentra la parte ya en la ciudad nueva: es el mismo cambio,
    // no una edición de otra persona.
    // Lo mismo con una ciudad que no es ni la esperada ni la de destino pero que el trabajo pidió: un intento anterior la llevó a
    // la que el sinónimo tenía entonces. No la toca; el consumidor guarda la que quedó y su paso 5 siembra la recorrección.
    [Fact]
    public void CorrectExternalShippingCityOfOurPartyInAnotherRequestedCityIsANoOp()
    {
        var cityC = Guid.CreateVersion7();
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty(cityC)));

        var changed = order.CorrectExternalShippingCity(ShippingParty(), DepartmentA, CityB, [cityC, CityB], Now.AddHours(1));

        Assert.False(changed);
        Assert.Equal(1, order.Version);
        Assert.Equal(ShippingParty(cityC), order.Shipping!.ToDetails());
    }

    // En una ciudad que el trabajo no pidió la puso una persona.
    [Fact]
    public void CorrectExternalShippingCityOfOurPartyInACityNobodyRequestedIsShippingPartyChanged()
    {
        var chosen = Guid.CreateVersion7();
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty(chosen)));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectExternalShippingCity(ShippingParty(), DepartmentA, CityB, [CityB], Now.AddHours(1)));

        Assert.Equal("order.order.shipping_party_changed", error.Code);
        Assert.Equal((ShippingParty(chosen), 1), (order.Shipping!.ToDetails(), order.Version));
    }

    [Fact]
    public void CorrectExternalShippingCityRetriedAfterItWasAppliedIsANoOp()
    {
        var order = NewShopifyOrder(parties: new QuotationParties(null, ShippingParty()));
        order.CorrectExternalShippingCity(ShippingParty(), DepartmentA, CityB, [CityB], Now.AddHours(1));

        var changed = order.CorrectExternalShippingCity(ShippingParty(), DepartmentA, CityB, [CityB], Now.AddHours(2));

        Assert.False(changed);
        Assert.Equal(2, order.Version);
        Assert.Equal(ShippingParty(CityB), order.Shipping!.ToDetails());
    }

    // Spec 2026-10-05 (facturar un pedido), decisiones 1 y 3, con los ejes de 2026-11-10: facturar es una marca
    // que deja quién y cuándo; no cambia el estado ni toca el pagado.
    [Fact]
    public void InvoiceAPaidOrderRecordsWhoAndWhenAndKeepsThePaidMark()
    {
        var order = NewOrder();
        Assert.Null(order.InvoicedAt);
        Assert.Null(order.InvoicedBy);
        var paidBy = new MemberId(Guid.CreateVersion7());
        order.MarkPaid(paidBy, requirePaymentProof: false, order.WithoutCosts(), Now);
        var invoicedBy = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(1);

        order.Invoice(invoicedBy, requireFullPayment: true, later);

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.True(order.IsInvoiced);
        Assert.Equal(later, order.InvoicedAt);
        Assert.Equal(invoicedBy, order.InvoicedBy);
        Assert.Equal(paidBy, order.PaidMarkedBy);
        Assert.Equal(Now, order.PaidMarkedAt);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(3, order.Version);
    }

    // Spec 2026-11-10 §3.4: sin el ajuste del tenant, facturar no mira el pago.
    [Fact]
    public void InvoiceWithoutTheFullPaymentRuleDoesNotRequireTheOrderToBePaid()
    {
        var order = NewOrder(proofs: []);

        order.Invoice(ConvertedBy, requireFullPayment: false, Now.AddDays(1));

        Assert.True(order.IsInvoiced);
        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
    }

    // Un anulado ya no es una venta: no se factura.
    [Fact]
    public void InvoiceRejectsACancelledOrder()
    {
        var cancelled = NewOrder();
        cancelled.Cancel(ConvertedBy, "El cliente desistió", Now);

        var fromCancelled = Assert.Throws<QuotationsDomainException>(() =>
            cancelled.Invoice(ConvertedBy, requireFullPayment: false, Now));

        Assert.Equal("order.order.already_cancelled", fromCancelled.Code);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.InvoicedBy);
    }

    // Facturar dos veces reescribiría quién y cuándo se facturó.
    [Fact]
    public void InvoiceTwiceIsRejectedAndKeepsTheFirstInvoicing()
    {
        var order = NewOrder();
        order.Invoice(ConvertedBy, requireFullPayment: false, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Invoice(new MemberId(Guid.CreateVersion7()), requireFullPayment: false, Now.AddDays(1)));

        Assert.Equal("order.order.already_invoiced", error.Code);
        Assert.Equal(ConvertedBy, order.InvoicedBy);
        Assert.Equal(Now, order.InvoicedAt);
        Assert.Equal(2, order.Version);
    }

    // Decisión 4: revertir limpia las marcas de facturado. El estado y el pagado quedan.
    [Fact]
    public void RevertInvoicingClearsTheMarksAndKeepsTheStatusAndThePaidMark()
    {
        var order = NewOrder();
        var paidBy = new MemberId(Guid.CreateVersion7());
        order.MarkPaid(paidBy, requirePaymentProof: false, order.WithoutCosts(), Now);
        order.Invoice(ConvertedBy, requireFullPayment: true, Now.AddDays(1));
        var later = Now.AddDays(2);

        order.RevertInvoicing(new MemberId(Guid.CreateVersion7()), later);

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.False(order.IsInvoiced);
        Assert.Null(order.InvoicedAt);
        Assert.Null(order.InvoicedBy);
        Assert.Equal(paidBy, order.PaidMarkedBy);
        Assert.Equal(Now, order.PaidMarkedAt);
        Assert.Equal(later, order.UpdatedAt);
        Assert.Equal(4, order.Version);
    }

    // Review Focus 3: el pedido revertido queda sin la marca. Se vuelve a facturar con el quién y
    // el cuándo nuevos, o se anula.
    [Fact]
    public void ARevertedOrderCanBeInvoicedAgainOrCancelled()
    {
        var reinvoiced = NewOrder();
        reinvoiced.Invoice(ConvertedBy, requireFullPayment: false, Now);
        reinvoiced.RevertInvoicing(ConvertedBy, Now.AddDays(1));
        var secondBiller = new MemberId(Guid.CreateVersion7());
        var later = Now.AddDays(2);

        reinvoiced.Invoice(secondBiller, requireFullPayment: false, later);

        Assert.True(reinvoiced.IsInvoiced);
        Assert.Equal(secondBiller, reinvoiced.InvoicedBy);
        Assert.Equal(later, reinvoiced.InvoicedAt);

        var cancelled = NewOrder();
        cancelled.Invoice(ConvertedBy, requireFullPayment: false, Now);
        cancelled.RevertInvoicing(ConvertedBy, Now.AddDays(1));

        cancelled.Cancel(ConvertedBy, "Facturado por error", later);

        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.InvoicedAt);
    }

    [Fact]
    public void RevertInvoicingRejectsAnOrderThatIsNotInvoiced()
    {
        var open = NewOrder();
        var paid = NewOrder();
        paid.MarkPaid(ConvertedBy, requirePaymentProof: false, paid.WithoutCosts(), Now);
        var cancelled = NewOrder();
        cancelled.Cancel(ConvertedBy, "El cliente desistió", Now);

        var fromOpen = Assert.Throws<QuotationsDomainException>(() =>
            open.RevertInvoicing(ConvertedBy, Now.AddDays(1)));
        var fromPaid = Assert.Throws<QuotationsDomainException>(() =>
            paid.RevertInvoicing(ConvertedBy, Now.AddDays(1)));
        var fromCancelled = Assert.Throws<QuotationsDomainException>(() =>
            cancelled.RevertInvoicing(ConvertedBy, Now.AddDays(1)));

        Assert.Equal("order.order.not_invoiced", fromOpen.Code);
        Assert.Equal("order.order.not_invoiced", fromPaid.Code);
        Assert.Equal("order.order.not_invoiced", fromCancelled.Code);
        Assert.Equal(OrderStatus.Open, paid.Status);
        Assert.Equal(2, paid.Version);
    }

    // Decisión 2: un facturado no se anula. El guard va antes que already_cancelled y que las reglas
    // del motivo (spec, «Dominio»): con el motivo vacío la respuesta sigue siendo already_invoiced.
    [Theory]
    [InlineData("El cliente desistió")]
    [InlineData(null)]
    [InlineData("   ")]
    public void CancelAnInvoicedOrderIsRejectedBeforeLookingAtTheReason(string? reason)
    {
        var order = NewOrder();
        order.Invoice(ConvertedBy, requireFullPayment: false, Now);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.Cancel(ConvertedBy, reason, Now.AddDays(1)));

        Assert.Equal("order.order.already_invoiced", error.Code);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancellationReason);
    }

    // Spec 2026-11-10 §3.6: facturado congela, así que nada de lo que sólo se permite mientras el pedido
    // se edita pasa. Tampoco se vuelve a marcar pagado: ya lo está.
    [Fact]
    public void AnInvoicedOrderRejectsEveryEditOnlyChange()
    {
        var order = NewOrder();
        var proofId = Assert.Single(order.PaymentProofs).Id;
        order.MarkPaid(ConvertedBy, requirePaymentProof: false, order.WithoutCosts(), Now);
        order.Invoice(ConvertedBy, requireFullPayment: true, Now);
        var later = Now.AddDays(1);

        var addProofs = Assert.Throws<QuotationsDomainException>(() =>
            order.AddPaymentProofs(
                [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)],
                null,
                ConvertedBy,
                later));
        var recalculate = Assert.Throws<QuotationsDomainException>(() =>
            order.RecalculatePaymentStatus(order.NetTotal, later));
        var removeProof = Assert.Throws<QuotationsDomainException>(() =>
            order.RemovePaymentProof(proofId, later));
        var markPaid = Assert.Throws<QuotationsDomainException>(() =>
            order.MarkPaid(ConvertedBy, requirePaymentProof: false, order.WithoutCosts(), later));
        var attach = Assert.Throws<QuotationsDomainException>(() =>
            order.AttachPaymentProofs([], ConvertedBy, later));
        var correct = Assert.Throws<QuotationsDomainException>(() =>
            order.CorrectPaymentProofs([], later));
        var notes = Assert.Throws<QuotationsDomainException>(() =>
            order.UpdateNotes("Otra nota", later));

        Assert.Equal("order.order.not_editable", addProofs.Code);
        Assert.Equal("order.order.not_editable", recalculate.Code);
        Assert.Equal("order.order.not_editable", removeProof.Code);
        Assert.Equal("order.order.already_paid", markPaid.Code);
        Assert.Equal("order.order.not_editable", attach.Code);
        Assert.Equal("order.order.not_editable", correct.Code);
        Assert.Equal("order.order.not_editable", notes.Code);
        Assert.True(order.IsInvoiced);
        Assert.Equal(3, order.Version);
    }

    // A pedido (2026-10-08): la fecha del comprobante la escribe quien lo adjunta —es la del
    // soporte de pago, no la del día en que se subió— y viaja con él desde que nace.
    [Fact]
    public void CreateKeepsThePaidOnDateOfEachProof()
    {
        var paidOn = new DateOnly(2026, 10, 6);

        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m, PaidOn: paidOn)]);

        Assert.Equal(paidOn, Assert.Single(order.PaymentProofs).PaidOn);
    }

    // Los comprobantes anteriores al campo no tienen fecha: null, no un valor inventado.
    [Fact]
    public void AProofWithoutPaidOnStaysWithoutADate()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);

        Assert.Null(Assert.Single(order.PaymentProofs).PaidOn);
    }

    [Fact]
    public void AddPaymentProofsKeepsThePaidOnDateOfTheNewProof()
    {
        var order = NewOrder(proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 10_000m)]);
        var fileId = Guid.CreateVersion7();
        var paidOn = new DateOnly(2026, 10, 7);

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(fileId, 30_000m, PaidOn: paidOn)],
            null,
            new MemberId(Guid.CreateVersion7()),
            Now.AddDays(1));

        var added = Assert.Single(order.PaymentProofs, proof => proof.FileId == fileId);
        Assert.Equal(paidOn, added.PaidOn);
    }

    [Fact]
    public void AttachPaymentProofsKeepsThePaidOnDateOfTheNewProof()
    {
        var order = NewOrder();
        var fileId = Guid.CreateVersion7();
        var paidOn = new DateOnly(2026, 10, 7);

        order.AttachPaymentProofs(
            [new OrderPaymentProofInput(fileId, 30_000m, PaidOn: paidOn)], new MemberId(Guid.CreateVersion7()), Now.AddDays(1));

        var added = Assert.Single(order.PaymentProofs, proof => proof.FileId == fileId);
        Assert.Equal(paidOn, added.PaidOn);
    }

    // A pedido (2026-10-08): al corregir un comprobante, la fecha del soporte se corrige con el
    // monto. La pantalla precarga las dos y manda las dos, así que el valor que viene reemplaza
    // al guardado —null la borra— igual que el monto, y no como `NewFileId`, que null conserva.
    [Fact]
    public void CorrectPaymentProofsUpdatesThePaidOnDate()
    {
        var order = NewOrder(
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m, PaidOn: new DateOnly(2026, 10, 1))]);
        var proof = Assert.Single(order.PaymentProofs);
        var paidOn = new DateOnly(2026, 10, 7);

        order.CorrectPaymentProofs(
            [new OrderPaymentProofAmountUpdate(proof.Id, 100_000m, PaidOn: paidOn)], Now.AddDays(1));

        Assert.Equal(paidOn, proof.PaidOn);
    }

    [Fact]
    public void CorrectPaymentProofsWithoutPaidOnClearsTheDate()
    {
        var order = NewOrder(
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 100_000m, PaidOn: new DateOnly(2026, 10, 1))]);
        var proof = Assert.Single(order.PaymentProofs);

        order.CorrectPaymentProofs([new OrderPaymentProofAmountUpdate(proof.Id, 100_000m)], Now.AddDays(1));

        Assert.Null(proof.PaidOn);
    }

    [Fact]
    public void AddPaymentProofsUpdatesThePaidOnDateOfAnExistingProof()
    {
        var order = NewOrder(
            proofs: [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)]);
        var existingProofId = Assert.Single(order.PaymentProofs).Id;
        var paidOn = new DateOnly(2026, 10, 7);

        order.AddPaymentProofs(
            [],
            null,
            ConvertedBy,
            Now.AddDays(1),
            [new OrderPaymentProofAmountUpdate(existingProofId, 60_000m, PaidOn: paidOn)]);

        var corrected = Assert.Single(order.PaymentProofs);
        Assert.Equal(60_000m, corrected.Amount);
        Assert.Equal(paidOn, corrected.PaidOn);
    }

    private static QuotationParties StoreParties()
    {
        var billing = new QuotationPartyDetails
        {
            Name = "Ana Pérez",
            IdentificationNumber = "1020304050",
            IdentificationType = PartyIdentificationType.Cc,
            PersonType = PartyPersonType.Natural,
            Phone = "+573001112233",
            Email = "ana@example.com",
            Address = "Calle 1 # 2-3",
            DepartmentId = Guid.CreateVersion7(),
            CityId = Guid.CreateVersion7(),
        };
        var shipping = billing with { IdentificationNumber = null, IdentificationType = null, PersonType = null };
        return new QuotationParties(billing, shipping, BillingVatSurplus: false);
    }

    private static Order NewBuyerStoreOrder(
        Guid? buyer, OrderPaymentStatus payment = OrderPaymentStatus.PaymentPending, string? notes = null)
    {
        var order = Order.CreateFromStore(
            OrderId.New(), TenantId, "PED-2026-0001", Header(isRetail: true), StoreParties(), Account, [Line()], notes, Advisor, Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime), "store-checkout:abc", buyerAccountId: buyer);
        if (payment != OrderPaymentStatus.PaymentPending)
        {
            order.SyncExternalPaymentStatus(payment, autoArchive: false, order.WithoutCosts(), Now);
        }

        return order;
    }

    [Theory]
    [InlineData("  Entregar en portería  ", "Entregar en portería")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void BuyerNotesKeepTheNormalizedCheckoutNote(string? note, string? expected)
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7(), notes: note);
        Assert.Equal(expected, order.BuyerNotes);
        Assert.Equal(order.Notes, order.BuyerNotes);
    }

    [Fact]
    public void BuyerNotesKeepInnerLineBreaks() =>
        Assert.Equal("Portería\nTorre 2", NewBuyerStoreOrder(Guid.CreateVersion7(), notes: "  Portería\nTorre 2  ").BuyerNotes);

    [Fact]
    public void BuyerNotesSurviveTheAdvisorNoteEdit()
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7(), notes: "Entregar en portería");

        order.UpdateNotes("Texto del asesor", Now.AddHours(1));

        Assert.Equal("Texto del asesor", order.Notes);
        Assert.Equal("Entregar en portería", order.BuyerNotes);
    }

    [Fact]
    public void BuyerNotesSurviveAPaymentProofWithAnotherNote()
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7(), notes: "Entregar en portería");

        order.AddPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 5_000m)],
            "Comprobante recibido", Advisor, Now.AddHours(1));

        Assert.Equal("Comprobante recibido", order.Notes);
        Assert.Equal("Entregar en portería", order.BuyerNotes);
    }

    [Fact]
    public void BuyerNotesSurviveShippingAndBillingEdits()
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7(), notes: "Entregar en portería");

        order.SetShipping(new ShippingDetails(null, "Coordinadora", FreightChargeMode.Charged, 15_000m, 19, false), Advisor, Now);
        order.SetBilling(null, true, false, null, Advisor, Now);

        Assert.Equal("Entregar en portería", order.BuyerNotes);
    }

    [Fact]
    public void BuyerNotesAreNullOutsideTheStore()
    {
        var manual = NewOrder(notes: "Nota manual");
        var converted = Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0001", ConvertedQuotation(),
            "Pago verificado", ConvertedBy, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));
        var shopify = NewShopifyOrder(notes: "Nota de Shopify");

        Assert.All([manual, converted, shopify], order =>
        {
            Assert.NotNull(order.Notes);
            Assert.Null(order.BuyerNotes);
        });
    }

    [Theory]
    [InlineData("open", OrderPaymentStatus.PaymentPending, true)]
    [InlineData("open", OrderPaymentStatus.PartialPaymentReceived, false)]
    [InlineData("open", OrderPaymentStatus.FullPaymentReceived, false)]
    [InlineData("paid", OrderPaymentStatus.PaymentPending, false)]
    [InlineData("cancelled", OrderPaymentStatus.PaymentPending, false)]
    [InlineData("invoiced", OrderPaymentStatus.PaymentPending, false)]
    [InlineData("prepared", OrderPaymentStatus.PaymentPending, false)]
    [InlineData("archived", OrderPaymentStatus.PaymentPending, false)]
    public void CanBeCancelledByBuyerCoversStatusByPayment(string step, OrderPaymentStatus payment, bool expected)
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7(), payment);
        MoveTo(order, step);
        Assert.Equal(expected, order.CanBeCancelledByBuyer);
    }

    // Lleva el pedido por un eje (spec 2026-11-10): «open» lo deja como nació.
    private static void MoveTo(Order order, string step)
    {
        switch (step)
        {
            case "paid":
                order.MarkPaid(Advisor, requirePaymentProof: false, order.WithoutCosts(), Now);
                break;
            case "invoiced":
                order.Invoice(Advisor, requireFullPayment: false, Now);
                break;
            case "prepared":
                order.Prepare(Advisor, Now);
                break;
            case "archived":
                order.Archive(Advisor, Now);
                break;
            case "cancelled":
                order.Cancel(Advisor, "Prueba", Now);
                break;
        }
    }

    [Fact]
    public void CancelByBuyerCancelsAPendingUnpaidOrderWithoutAMember()
    {
        var buyer = Guid.CreateVersion7();
        var order = NewBuyerStoreOrder(buyer);
        var version = order.Version;

        order.CancelByBuyer(buyer, Now.AddHours(1));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Null(order.CancelledBy);
        Assert.Equal(Now.AddHours(1), order.CancelledAt);
        Assert.Equal(Now.AddHours(1), order.UpdatedAt);
        Assert.Equal("Cancelado por el comprador desde la tienda", order.CancellationReason);
        Assert.Equal(version + 1, order.Version);
    }

    [Theory]
    [InlineData(OrderPaymentStatus.PartialPaymentReceived)]
    [InlineData(OrderPaymentStatus.FullPaymentReceived)]
    public void CancelByBuyerRejectsAPendingOrderWithPayment(OrderPaymentStatus payment)
    {
        var buyer = Guid.CreateVersion7();
        AssertNotCancellableByBuyer(NewBuyerStoreOrder(buyer, payment), buyer);
    }

    [Theory]
    [InlineData("paid")]
    [InlineData("cancelled")]
    [InlineData("invoiced")]
    [InlineData("prepared")]
    [InlineData("archived")]
    public void CancelByBuyerRejectsAnOrderSomeoneAlreadyTouched(string step)
    {
        var buyer = Guid.CreateVersion7();
        var order = NewBuyerStoreOrder(buyer);
        MoveTo(order, step);
        AssertNotCancellableByBuyer(order, buyer);
    }

    [Fact]
    public void AnOrderWithARegisteredProofIsNotCancellableByTheBuyerEvenIfStillPaymentPending()
    {
        var buyer = Guid.CreateVersion7();
        var order = NewBuyerStoreOrder(buyer);
        // AttachPaymentProofs no recalcula el pago: deja el comprobante con el pago aún en PaymentPending.
        order.AttachPaymentProofs(
            [new OrderPaymentProofInput(Guid.CreateVersion7(), 50_000m)],
            Advisor,
            Now.AddMinutes(5));

        Assert.Equal(OrderPaymentStatus.PaymentPending, order.PaymentStatus);
        Assert.False(order.CanBeCancelledByBuyer);
        AssertNotCancellableByBuyer(order, buyer);
    }

    [Fact]
    public void CancelByBuyerRejectsAnotherBuyer() =>
        AssertNotCancellableByBuyer(NewBuyerStoreOrder(Guid.CreateVersion7()), Guid.CreateVersion7());

    [Fact]
    public void CancelByBuyerRejectsAnOrderWithoutBuyer() =>
        AssertNotCancellableByBuyer(NewBuyerStoreOrder(null), Guid.CreateVersion7());

    [Fact]
    public void LinkBuyerAccountFillsAnEmptyBuyerWithoutTouchingVersionOrState()
    {
        var order = NewBuyerStoreOrder(null);
        var (version, updatedAt, status) = (order.Version, order.UpdatedAt, order.Status);
        var buyer = Guid.CreateVersion7();

        Assert.True(order.LinkBuyerAccount(buyer));

        Assert.Equal(buyer, order.BuyerAccountId);
        Assert.Equal((version, updatedAt, status), (order.Version, order.UpdatedAt, order.Status));
    }

    [Fact]
    public void LinkBuyerAccountNeverReplacesAnExistingBuyer()
    {
        var buyer = Guid.CreateVersion7();
        var order = NewBuyerStoreOrder(buyer);

        Assert.False(order.LinkBuyerAccount(Guid.CreateVersion7()));
        Assert.False(order.LinkBuyerAccount(buyer));
        Assert.Equal(buyer, order.BuyerAccountId);
    }

    // Spec 2026-11-15 §3.3: la decisión del asesor manda; el enlace automático ya no toca la cuenta.
    [Fact]
    public void LinkBuyerAccountIsRefusedAfterAMemberDecision()
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7());
        order.SetBuyerAccountByMember(null, Now.AddHours(1));

        Assert.False(order.LinkBuyerAccount(Guid.CreateVersion7()));
        Assert.Null(order.BuyerAccountId);
    }

    [Fact]
    public void SetBuyerAccountByMemberReplacesMarksAndBumpsVersion()
    {
        var order = NewBuyerStoreOrder(Guid.CreateVersion7());
        var version = order.Version;
        var buyer = Guid.CreateVersion7();

        Assert.True(order.SetBuyerAccountByMember(buyer, Now.AddHours(1)));

        Assert.Equal(buyer, order.BuyerAccountId);
        Assert.True(order.BuyerAccountSetByMember);
        Assert.Equal(version + 1, order.Version);
        Assert.Equal(Now.AddHours(1), order.UpdatedAt);
    }

    // Quitar sobre un pedido sin cuenta significa «no lo enlaces solo», y esa decisión vale (spec §4).
    [Fact]
    public void RemovingFromAnOrderWithoutAccountStillMarks()
    {
        var order = NewBuyerStoreOrder(null);

        Assert.True(order.SetBuyerAccountByMember(null, Now.AddHours(1)));

        Assert.Null(order.BuyerAccountId);
        Assert.True(order.BuyerAccountSetByMember);
    }

    [Fact]
    public void SetBuyerAccountByMemberWithTheSameAccountTwiceChangesNothing()
    {
        var order = NewBuyerStoreOrder(null);
        var buyer = Guid.CreateVersion7();
        Assert.True(order.SetBuyerAccountByMember(buyer, Now.AddHours(1)));
        var version = order.Version;

        Assert.False(order.SetBuyerAccountByMember(buyer, Now.AddHours(2)));

        Assert.Equal(version, order.Version);
        Assert.Equal(buyer, order.BuyerAccountId);
    }

    [Fact]
    public void SetBuyerAccountByMemberOnANonStoreOrderIsSourceInvalid()
    {
        var order = NewShopifyOrder();

        var error = Assert.Throws<QuotationsDomainException>(
            () => order.SetBuyerAccountByMember(Guid.CreateVersion7(), Now.AddHours(1)));

        Assert.Equal("order.order.source_invalid", error.Code);
        Assert.Null(order.BuyerAccountId);
        Assert.False(order.BuyerAccountSetByMember);
    }

    private static void AssertNotCancellableByBuyer(Order order, Guid buyer)
    {
        var status = order.Status;
        var ex = Assert.Throws<QuotationsDomainException>(() => order.CancelByBuyer(buyer, Now.AddHours(1)));
        Assert.Equal("order.order.not_cancellable_by_buyer", ex.Code);
        Assert.Equal(status, order.Status);
    }

    // ---- Pedidos de proyecto (spec 2026-11-26) ----

    private static Quotation ConvertedProjectQuotation()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0002", ClientId, Advisor, new DateOnly(2026, 11, 1), paymentMethod: null,
            notes: null, QuotationParties.Empty, Account, "COP", customerVatSurplus: false, customerWithholdings: [],
            MunicipalContext.None, Advisor, Now, QuotationKind.Project);
        var service = new ProjectServiceLine(
            Guid.CreateVersion7(), Guid.CreateVersion7(), 19, ProductKind.Service, SalesUnit.Service);
        var scope = ProjectScope.Create(
            service.VariantId, ProjectPricingMode.PerPhase, null,
            [
                new ProjectPhase(Guid.CreateVersion7(), "Descubrimiento", 600_000m,
                    [new ProjectItem(Guid.CreateVersion7(), "Talleres")]),
                new ProjectPhase(Guid.CreateVersion7(), "Construcción", 400_000m, []),
            ],
            []);
        quotation.ReplaceProjectScope(scope, service, Advisor, Now);
        quotation.ConvertToOrder(Advisor, Now, new Dictionary<Guid, QuotationProductSnapshot>
        {
            [service.VariantId] = new("DEV", "Desarrollo de software"),
        });

        return quotation;
    }

    private static Order ProjectOrder(Quotation quotation) =>
        Order.FromQuotation(
            OrderId.New(), TenantId, "PED-2026-0002", quotation, null, ConvertedBy, [], Now,
            orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

    [Fact]
    public void OrderFromAProjectCopiesKindScopeAndLine()
    {
        var quotation = ConvertedProjectQuotation();

        var order = ProjectOrder(quotation);

        Assert.Equal(QuotationKind.Project, order.Kind);
        Assert.Equal(quotation.ProjectScope, order.ProjectScope);
        Assert.Equal(
            quotation.ProjectScope!.Phases.Select(phase => phase.Id), order.ProjectScope!.Phases.Select(phase => phase.Id));
        Assert.Equal(
            quotation.ProjectScope.Phases[0].Items.Select(item => item.Id),
            order.ProjectScope.Phases[0].Items.Select(item => item.Id));
        var quoted = Assert.Single(quotation.Items);
        var line = Assert.Single(order.Items);
        Assert.Equal(quoted.VariantId, line.VariantId);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(quoted.UnitPrice, line.UnitPrice);
        Assert.Equal(1_000_000m, line.UnitPrice);
        Assert.Equal(ProductKind.Service, line.Kind);
        Assert.Equal("Desarrollo de software", line.ProductName);
        Assert.Equal(quotation.Total, order.Total);
    }

    [Fact]
    public void AnOrderFromAStandardQuotationHasNoProjectScope()
    {
        var order = ProjectOrder(ConvertedQuotation());

        Assert.Equal(QuotationKind.Standard, order.Kind);
        Assert.Null(order.ProjectScope);
    }

    [Fact]
    public void AProjectOrderLocksItsLines()
    {
        var order = ProjectOrder(ConvertedProjectQuotation());
        var line = order.Items.Single();

        QuotationsDomainException[] errors =
        [
            Assert.Throws<QuotationsDomainException>(() => order.AddItem(
                OrderItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 1m, 0m, 19, OrderLineCosts.None,
                Advisor, Now)),
            Assert.Throws<QuotationsDomainException>(() => order.AddGift(
                OrderItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 1m, 19, OrderLineCosts.None,
                Advisor, Now)),
            Assert.Throws<QuotationsDomainException>(
                () => order.UpdateItemQuantity(line.Id, 2m, 0m, 19, Advisor, Now)),
            Assert.Throws<QuotationsDomainException>(() => order.ChangeItemUnitPrice(line.Id, 5m, Advisor, Now)),
            Assert.Throws<QuotationsDomainException>(() => order.RemoveItem(line.Id, Advisor, Now)),
        ];

        Assert.All(errors, error => Assert.Equal("order.project.items_locked", error.Code));
        Assert.Equal(1m, Assert.Single(order.Items).Quantity);
        Assert.False(order.RequiresShipping);
    }
}
