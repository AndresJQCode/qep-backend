using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.TestWithholdings;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El pedido con municipio de la operación (spec ReteICA por empresa §7.5–§7.7): lo hereda al convertir, nace en el de la
/// empresa, re-resuelve con la regla del cliente al cambiar empresa o municipio, y deja editar el porcentaje mientras es
/// editable.
/// </summary>
public sealed class OrderMunicipalWithholdingsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid Bogota = Guid.Parse("01900000-0000-7000-8000-0000000b0901");
    private static readonly Guid Medellin = Guid.Parse("01900000-0000-7000-8000-0000000e0d01");

    private static readonly WithholdingRate ReteIcaBogota = Rate("ReteICA Bogotá", "ReteICA", 0.966m, Bogota);
    private static readonly WithholdingRate ReteIcaMedellin = Rate("ReteICA Medellín", "ReteICA", 1m, Medellin);
    private static readonly WithholdingRate Retefuente = RetefuenteCo;

    private static readonly QuotationBillingAccount AccountBogota = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
        CityId = Bogota,
    };

    private static readonly QuotationPartyDetails OwnBillingParty = new()
    {
        Name = "Sede administrativa",
        IdentificationNumber = "1020304050",
        IdentificationType = PartyIdentificationType.Nit,
        Phone = "3105550134",
        Email = "compras@sede.co",
        Address = "Calle 10 # 45-12",
        DepartmentId = Guid.CreateVersion7(),
        CityId = Guid.CreateVersion7(),
    };

    private static MunicipalContext Context(Guid? city, WithholdingRate? companyRate, bool agent) =>
        new(city, [new MunicipalOffer("ReteICA", companyRate, agent)]);

    private static OrderHeader Header(params WithholdingRate[] withholdings) =>
        new(ClientId, AdvisorId, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, withholdings);

    // 119.000 con el IVA del 19 % adentro: base sin IVA 100.000.
    private static OrderLineInput Line() =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"));

    private static Order NewOrder(OrderHeader? header = null, QuotationBillingAccount? account = null) =>
        Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001", header ?? Header(Retefuente, ReteIcaBogota), QuotationParties.Empty,
            account ?? AccountBogota, ShippingDetails.None, [Line()], null, AdvisorId, [], Now);

    [Fact]
    public void ConvertingInheritsCityRowsAndEditedPercentage()
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 12, 31), null, null,
            QuotationParties.Empty, AccountBogota, "COP", customerVatSurplus: false, [Retefuente],
            Context(Bogota, ReteIcaBogota, agent: true), AdvisorId, Now);
        quotation.AddItem(QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, AdvisorId, Now);
        quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);

        var order = Order.FromQuotation(OrderId.New(), TenantId, "PED-2026-0001", quotation, null, AdvisorId, [], Now);

        Assert.Equal(Bogota, order.OperationCityId);
        var row = order.Withholdings.Single(w => w.Type == "ReteICA");
        Assert.Equal((0.8m, 0.966m, Bogota), (row.Percentage, row.CatalogPercentage, row.CityId));
        Assert.True(row.Overridden);
        Assert.Equal(quotation.RetentionAmount, order.RetentionAmount);
        Assert.Equal(3_300m, order.RetentionAmount);
    }

    [Fact]
    public void StoreAndManualOrdersStartAtTheCompanysCity()
    {
        var manual = NewOrder();
        var store = Order.CreateFromStore(
            OrderId.New(), TenantId, "PED-2026-0002", Header(Retefuente, ReteIcaBogota),
            new QuotationParties(OwnBillingParty, OwnBillingParty with { }, BillingVatSurplus: false), AccountBogota,
            [Line()], null, AdvisorId, Now, externalKey: "checkout-1");

        foreach (var order in new[] { manual, store })
        {
            Assert.Equal(Bogota, order.OperationCityId);
            Assert.Contains(order.AppliedWithholdings, w => w.Rate == ReteIcaBogota && w.Amount == 966m);
        }
    }

    [Fact]
    public void ACompanyWithoutCityOrOfACountryWithoutMunicipalTypesStartsWithoutCity()
    {
        Assert.Null(NewOrder(Header(Retefuente), AccountBogota with { CityId = null }).OperationCityId);
        Assert.Null(NewOrder(Header(Retefuente), AccountBogota with { Country = "ES" }).OperationCityId);
    }

    public static TheoryData<string> NonEditableStates => ["prepared", "invoiced"];

    [Theory]
    [MemberData(nameof(NonEditableStates))]
    public void PreparedOrInvoicedRejectsCityAndPercentage(string state)
    {
        var order = NewOrder();
        if (state == "prepared")
        {
            order.Prepare(AdvisorId, Now);
        }
        else
        {
            order.Invoice(AdvisorId, requireFullPayment: false, Now);
        }

        var city = Assert.Throws<QuotationsDomainException>(() =>
            order.ApplyBillingContext(null, Context(Medellin, ReteIcaMedellin, agent: true), AdvisorId, Now));
        var percentage = Assert.Throws<QuotationsDomainException>(() =>
            order.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now));
        var clear = Assert.Throws<QuotationsDomainException>(() =>
            order.ClearMunicipalWithholdings(["ReteICA"], AdvisorId, Now));

        Assert.Equal("order.order.not_editable", city.Code);
        Assert.Equal("order.order.not_editable", percentage.Code);
        Assert.Equal("order.order.not_editable", clear.Code);
        Assert.Equal(Bogota, order.OperationCityId);
    }

    [Fact]
    public void ChangingTheCityRemovesAHandAppliedRate()
    {
        var order = NewOrder(Header(Retefuente));
        order.SetWithholdings([Retefuente, ReteIcaBogota], AdvisorId, Now);

        var change = order.ApplyBillingContext(
            null, Context(Medellin, companyRate: ReteIcaMedellin, agent: false), AdvisorId, Now);

        Assert.False(change.AccountChanged);
        Assert.True(change.Municipal.Triggered);
        Assert.Equal((Bogota, Medellin), (change.Municipal.OperationCityBefore, change.Municipal.OperationCityAfter));
        Assert.Equal(Medellin, order.OperationCityId);
        Assert.Equal([Retefuente], order.Withholdings.OrderBy(w => w.Position).Select(w => w.Rate));
        Assert.Equal(ReteIcaBogota, Assert.Single(change.Municipal.RemovedManual));
        Assert.Empty(change.Municipal.DiscardedOverrides);
        Assert.Equal(2_500m, order.RetentionAmount);
    }

    [Fact]
    public void ChangingTheCityAppliesTheNewCitysRateWhenTheCustomerIsAnAgent()
    {
        var order = NewOrder();

        var change = order.ApplyBillingContext(null, Context(Medellin, ReteIcaMedellin, agent: true), AdvisorId, Now);

        Assert.True(change.Municipal.Triggered);
        Assert.Equal([Retefuente, ReteIcaMedellin], order.Withholdings.OrderBy(w => w.Position).Select(w => w.Rate));
        Assert.Empty(change.Municipal.RemovedManual);
        Assert.Equal(3_500m, order.RetentionAmount);
    }

    [Fact]
    public void TheSameCityAndCompanyDoNotReResolve()
    {
        var order = NewOrder(Header(Retefuente));
        order.SetWithholdings([Retefuente, ReteIcaBogota], AdvisorId, Now);
        var version = order.Version;

        var change = order.ApplyBillingContext(AccountBogota, Context(Bogota, ReteIcaBogota, agent: false), AdvisorId, Now);

        Assert.Equal((false, false), (change.AccountChanged, change.Municipal.Triggered));
        Assert.Equal([Retefuente, ReteIcaBogota], order.Withholdings.OrderBy(w => w.Position).Select(w => w.Rate));
        Assert.Equal(version, order.Version);
    }

    // Un pedido anterior (cuenta sin municipio) que reenvía la misma cuenta completa el municipio sin contar como cambio.
    [Fact]
    public void ResendingTheSameAccountFillsItsCityWithoutCountingAsAChange()
    {
        var order = NewOrder(Header(Retefuente), AccountBogota with { CityId = null });
        var version = order.Version;

        var change = order.ApplyBillingContext(AccountBogota, MunicipalContext.None, AdvisorId, Now);

        Assert.Equal((false, false), (change.AccountChanged, change.Municipal.Triggered));
        Assert.Equal(Bogota, order.BillingAccount!.CityId);
        Assert.Null(order.OperationCityId);
        Assert.Equal(version, order.Version);
    }

    [Fact]
    public void ChangingTheCompanyResetsTheCityUnlessExplicit()
    {
        var order = NewOrder();
        var accountMedellin = AccountBogota with { CompanyId = Guid.CreateVersion7(), CityId = Medellin };

        Assert.Equal(Medellin, order.OperationCityFor(accountMedellin, OperationCityChoice.Keep));
        Assert.Equal(Bogota, order.OperationCityFor(accountMedellin, OperationCityChoice.Explicit(Bogota)));
        Assert.Equal(Bogota, order.OperationCityFor(null, OperationCityChoice.Keep));
        Assert.Equal(Medellin, order.OperationCityFor(null, OperationCityChoice.Explicit(Medellin)));
        Assert.Equal(Bogota, order.OperationCityFor(null, OperationCityChoice.Company));

        var change = order.ApplyBillingContext(
            accountMedellin, Context(Medellin, ReteIcaMedellin, agent: true), AdvisorId, Now);

        Assert.True(change.AccountChanged);
        Assert.True(change.Municipal.Triggered);
        Assert.Equal(accountMedellin, order.BillingAccount);
        Assert.Equal([Retefuente, ReteIcaMedellin], order.Withholdings.OrderBy(w => w.Position).Select(w => w.Rate));
    }

    // Review Focus 4.
    [Fact]
    public void CompanyOfACountryWithoutMunicipalTypesDropsMunicipalRows()
    {
        var order = NewOrder();
        order.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);
        var spanish = AccountBogota with { CompanyId = Guid.CreateVersion7(), Country = "ES" };

        Assert.Null(order.OperationCityFor(spanish, OperationCityChoice.Explicit(Bogota)));

        var change = order.ApplyBillingContext(spanish, MunicipalContext.None, AdvisorId, Now);

        Assert.True(change.AccountChanged);
        Assert.Null(order.OperationCityId);
        Assert.DoesNotContain(order.Withholdings, w => w.Type == "ReteICA");
        Assert.Equal(0.8m, Assert.Single(change.Municipal.DiscardedOverrides).Percentage);
        Assert.Empty(change.Municipal.RemovedManual);
    }

    [Fact]
    public void CurrencyChangeIsStill422()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.ApplyBillingContext(AccountBogota with { Currency = "USD" }, MunicipalContext.None, AdvisorId, Now));

        Assert.Equal("order.billing.currency_change_not_allowed", error.Code);
        Assert.Equal(Bogota, order.OperationCityId);
    }

    [Fact]
    public void SelectorWithAMunicipalRateOfAnotherCityIs422()
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetWithholdings([Retefuente, ReteIcaMedellin], AdvisorId, Now));

        Assert.Equal("order.billing.withholding_tax_rate_invalid", error.Code);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.8125")]
    [InlineData("100.001")]
    public void SetWithholdingPercentageRejectsAnInvalidValue(string value)
    {
        var order = NewOrder();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            order.SetWithholdingPercentage(
                "ReteICA", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), AdvisorId, Now));

        Assert.Equal("order.billing.withholding_percentage_invalid", error.Code);
    }

    [Fact]
    public void SetWithholdingPercentageNeedsAMunicipalRow()
    {
        var national = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder().SetWithholdingPercentage("Retefuente", 2m, AdvisorId, Now));
        var withoutRow = Assert.Throws<QuotationsDomainException>(() =>
            NewOrder(Header(Retefuente)).SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now));

        Assert.Equal("order.billing.withholding_percentage_not_editable", national.Code);
        Assert.Equal("order.billing.withholding_percentage_not_editable", withoutRow.Code);
    }

    [Fact]
    public void SetWithholdingPercentageEditsAndRestores()
    {
        var order = NewOrder();
        var version = order.Version;

        Assert.NotNull(order.SetWithholdingPercentage("ReteICA", 100m, AdvisorId, Now));
        var edited = order.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);

        Assert.Equal(new WithholdingPercentageChange("ReteICA Bogotá", 100m, 0.8m, Restored: false), edited);
        Assert.Equal(3_300m, order.RetentionAmount);
        Assert.Equal(order.Total - 3_300m, order.NetTotal);
        Assert.Equal(version + 2, order.Version);
        Assert.Null(order.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now));

        var restored = order.SetWithholdingPercentage("ReteICA", null, AdvisorId, Now);

        Assert.Equal(new WithholdingPercentageChange("ReteICA Bogotá", 0.8m, 0.966m, Restored: true), restored);
        Assert.Equal(3_466m, order.RetentionAmount);
    }

    [Fact]
    public void ClearMunicipalWithholdingsRemovesTheRow()
    {
        var order = NewOrder();
        order.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);
        var version = order.Version;

        var removed = order.ClearMunicipalWithholdings(["ReteICA"], AdvisorId, Now);

        Assert.Equal(0.8m, Assert.Single(removed).Percentage);
        Assert.Equal([Retefuente], order.Withholdings.OrderBy(w => w.Position).Select(w => w.Rate));
        Assert.Equal(2_500m, order.RetentionAmount);
        Assert.Equal(version + 1, order.Version);

        Assert.Empty(order.ClearMunicipalWithholdings(["ReteICA"], AdvisorId, Now));
        Assert.Equal(version + 1, order.Version);
    }
}
