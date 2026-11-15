using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.TestWithholdings;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// La cotización con municipio de la operación (spec ReteICA por empresa §7.5–§7.7): la fuente del cliente por la regla
/// del cliente, la re-resolución al cambiar empresa, municipio o cliente, el porcentaje editado y «Sin retención».
/// </summary>
public sealed class QuotationMunicipalWithholdingsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly ValidUntil = new(2026, 12, 31);

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

    private static readonly QuotationBillingAccount AccountMedellin = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Davivienda",
        AccountNumber = "87654321",
        Currency = "COP",
        Country = "CO",
        CityId = Medellin,
    };

    private static readonly QuotationBillingAccount SpanishAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Santander",
        AccountNumber = "ES001",
        Currency = "COP",
        Country = "ES",
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

    private static Quotation NewQuotation(
        QuotationBillingAccount? account = null,
        MunicipalContext? municipal = null,
        IReadOnlyList<WithholdingRate>? customerWithholdings = null) =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "COT-2026-0001",
            Guid.CreateVersion7(),
            AdvisorId,
            ValidUntil,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            account,
            "COP",
            customerVatSurplus: false,
            customerWithholdings ?? [Retefuente],
            municipal ?? MunicipalContext.None,
            AdvisorId,
            Now);

    // 119.000 con el IVA del 19 % adentro: base sin IVA 100.000.
    private static void AddLine(Quotation quotation, decimal unitPrice = 119_000m) =>
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), quantity: 1, unitPrice,
            discountPercentage: 0m, taxPercentage: 19, AdvisorId, Now);

    private static MunicipalReResolution UseClientData(
        Quotation quotation, QuotationBillingAccount? account, MunicipalContext municipal) =>
        quotation.UpdateDetails(
            ValidUntil, null, null, QuotationParties.Empty, account, null, isRetail: false, globalScaleFloor: null,
            municipal, AdvisorId, Now);

    private static MunicipalReResolution UseOwnBilling(
        Quotation quotation, QuotationBillingAccount? account, MunicipalContext municipal) =>
        quotation.UpdateDetails(
            ValidUntil, null, null, new QuotationParties(OwnBillingParty, null, BillingVatSurplus: false), account, null,
            isRetail: false, globalScaleFloor: null, municipal, AdvisorId, Now);

    private static Quotation QuotationWithReteIcaBogota()
    {
        var quotation = NewQuotation(AccountBogota, Context(Bogota, ReteIcaBogota, agent: true));
        AddLine(quotation);
        return quotation;
    }

    [Fact]
    public void FixingTheCompanySetsTheOperationCityToTheCompanys()
    {
        var quotation = NewQuotation();
        AddLine(quotation);
        Assert.Null(quotation.OperationCityId);

        var result = UseClientData(quotation, AccountBogota, Context(Bogota, ReteIcaBogota, agent: true));

        Assert.True(result.Triggered);
        Assert.Null(result.OperationCityBefore);
        Assert.Equal(Bogota, result.OperationCityAfter);
        Assert.Equal(Bogota, quotation.OperationCityId);
        Assert.Equal([Retefuente, ReteIcaBogota], quotation.CustomerWithholdings.Select(w => w.Rate));
        // 2.500 de Retefuente y 966 de ReteICA sobre la base de 100.000.
        Assert.Equal(3_466m, quotation.RetentionAmount);
    }

    [Fact]
    public void CreateAppliesTheCustomerRuleAndKeepsOnlyNationalCustomerRates()
    {
        // Un cliente ya no tiene tasas municipales: si llegara una en la foto, no entra.
        var quotation = NewQuotation(
            AccountBogota, Context(Bogota, ReteIcaBogota, agent: false), [Retefuente, ReteIcaMedellin]);

        Assert.Equal(Bogota, quotation.OperationCityId);
        Assert.Equal([Retefuente], quotation.CustomerWithholdings.Select(w => w.Rate));
    }

    [Fact]
    public void OperationCityAtCreationFollowsTheChoice()
    {
        Assert.Equal(Bogota, Quotation.OperationCityAtCreation(AccountBogota, OperationCityChoice.Keep));
        Assert.Equal(Bogota, Quotation.OperationCityAtCreation(AccountBogota, OperationCityChoice.Company));
        Assert.Equal(Medellin, Quotation.OperationCityAtCreation(AccountBogota, OperationCityChoice.Explicit(Medellin)));
        Assert.Null(Quotation.OperationCityAtCreation(null, OperationCityChoice.Explicit(Medellin)));
        Assert.Null(Quotation.OperationCityAtCreation(SpanishAccount, OperationCityChoice.Explicit(Bogota)));

        var quotation = NewQuotation(AccountBogota, Context(Medellin, ReteIcaMedellin, agent: true));

        var result = UseClientData(quotation, AccountBogota, Context(Medellin, ReteIcaMedellin, agent: true));

        Assert.False(result.Triggered);
        Assert.Equal(Medellin, quotation.OperationCityId);
        Assert.Equal([Retefuente, ReteIcaMedellin], quotation.CustomerWithholdings.Select(w => w.Rate));
    }

    [Fact]
    public void ChangingTheCompanyResetsTheCityUnlessExplicit()
    {
        var quotation = NewQuotation(AccountBogota, Context(Bogota, ReteIcaBogota, agent: true));

        Assert.Equal(Medellin, quotation.OperationCityFor(AccountMedellin, OperationCityChoice.Keep));
        Assert.Equal(Bogota, quotation.OperationCityFor(AccountMedellin, OperationCityChoice.Explicit(Bogota)));
        Assert.Equal(Medellin, quotation.OperationCityFor(AccountMedellin, OperationCityChoice.Company));
        Assert.Equal(Bogota, quotation.OperationCityFor(AccountBogota with { BankName = "Otro" }, OperationCityChoice.Keep));
        Assert.Equal(Medellin, quotation.OperationCityFor(AccountBogota, OperationCityChoice.Explicit(Medellin)));
        Assert.Null(quotation.OperationCityFor(null, OperationCityChoice.Keep));
    }

    [Fact]
    public void ChangingTheCityReResolvesAndDiscardsTheEditedPercentage()
    {
        var quotation = QuotationWithReteIcaBogota();
        quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);

        var result = UseClientData(quotation, AccountBogota, Context(Medellin, companyRate: null, agent: true));

        Assert.True(result.Triggered);
        Assert.Equal(Bogota, result.OperationCityBefore);
        Assert.Equal(Medellin, result.OperationCityAfter);
        Assert.Equal(Medellin, quotation.OperationCityId);
        Assert.Equal([Retefuente], quotation.CustomerWithholdings.Select(w => w.Rate));
        var discarded = Assert.Single(result.DiscardedOverrides);
        Assert.Equal(0.8m, discarded.Percentage);
        Assert.Equal(0.966m, discarded.CatalogPercentage);
        Assert.Empty(result.RemovedNotAgent);
        Assert.Equal(2_500m, quotation.RetentionAmount);
    }

    [Fact]
    public void ChangingTheCityMovesTheCustomerRowToTheNewCitysRate()
    {
        var quotation = QuotationWithReteIcaBogota();

        var result = UseClientData(quotation, AccountBogota, Context(Medellin, ReteIcaMedellin, agent: true));

        Assert.True(result.Triggered);
        Assert.Empty(result.DiscardedOverrides);
        Assert.Equal([Retefuente, ReteIcaMedellin], quotation.CustomerWithholdings.Select(w => w.Rate));
        Assert.Equal(3_500m, quotation.RetentionAmount);
    }

    [Fact]
    public void ChangeClientReResolvesWithTheNewCustomersAgents()
    {
        var quotation = QuotationWithReteIcaBogota();

        var result = quotation.ChangeClient(
            Guid.CreateVersion7(), customerVatSurplus: false, [Retefuente],
            Context(Bogota, ReteIcaBogota, agent: false), AdvisorId, Now);

        Assert.True(result.Triggered);
        Assert.Equal(Bogota, quotation.OperationCityId);
        Assert.Equal([Retefuente], quotation.CustomerWithholdings.Select(w => w.Rate));
        Assert.Equal(2_500m, quotation.RetentionAmount);

        quotation.ChangeClient(
            Guid.CreateVersion7(), customerVatSurplus: false, [Retefuente],
            Context(Bogota, ReteIcaBogota, agent: true), AdvisorId, Now);

        Assert.Equal([Retefuente, ReteIcaBogota], quotation.CustomerWithholdings.Select(w => w.Rate));
    }

    // Plan P5 (spec §7.6): con facturación propia el selector municipal pasa a la tasa de la empresa para el municipio
    // nuevo, aunque el cliente no sea agente; si la empresa no tiene, queda vacío; un selector vacío sigue vacío.
    [Fact]
    public void OwnBillingSelectorMovesToTheNewCitysRateOrEmpties()
    {
        var quotation = QuotationWithReteIcaBogota();
        UseOwnBilling(quotation, AccountBogota, Context(Bogota, ReteIcaBogota, agent: false));
        quotation.SetBillingWithholdings([Retefuente, ReteIcaBogota], AdvisorId, Now);

        UseOwnBilling(quotation, AccountBogota, Context(Medellin, ReteIcaMedellin, agent: false));

        Assert.Equal([Retefuente, ReteIcaMedellin], quotation.BillingWithholdings.Select(w => w.Rate));
        Assert.Equal(3_500m, quotation.RetentionAmount);

        UseOwnBilling(quotation, AccountBogota, Context(Bogota, companyRate: null, agent: true));

        Assert.Equal([Retefuente], quotation.BillingWithholdings.Select(w => w.Rate));

        UseOwnBilling(quotation, AccountBogota, Context(Medellin, ReteIcaMedellin, agent: true));

        Assert.Equal([Retefuente], quotation.BillingWithholdings.Select(w => w.Rate));
    }

    [Fact]
    public void CountryWithoutMunicipalTypesDropsMunicipalRowsAndCity()
    {
        var quotation = QuotationWithReteIcaBogota();
        quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);

        Assert.Null(quotation.OperationCityFor(SpanishAccount, OperationCityChoice.Explicit(Bogota)));

        var result = UseClientData(quotation, SpanishAccount, MunicipalContext.None);

        Assert.True(result.Triggered);
        Assert.Null(quotation.OperationCityId);
        Assert.Equal([Retefuente], quotation.CustomerWithholdings.Select(w => w.Rate));
        Assert.Equal(0.8m, Assert.Single(result.DiscardedOverrides).Percentage);
    }

    // Review Focus 2: un documento anterior (cuenta sin municipio, sin municipio de la operación) que reenvía la misma
    // empresa completa la ciudad de la empresa sin re-resolver.
    [Fact]
    public void ResendingTheSameCompanyFillsCityWithoutTriggering()
    {
        var legacy = NewQuotation(AccountBogota with { CityId = null });
        AddLine(legacy);
        Assert.Null(legacy.OperationCityId);
        Assert.Null(legacy.OperationCityFor(AccountBogota, OperationCityChoice.Keep));

        var result = UseClientData(legacy, AccountBogota, Context(null, ReteIcaBogota, agent: true));

        Assert.False(result.Triggered);
        Assert.Equal(Bogota, legacy.BillingAccount!.CityId);
        Assert.Null(legacy.OperationCityId);
        Assert.Equal([Retefuente], legacy.CustomerWithholdings.Select(w => w.Rate));
    }

    [Fact]
    public void ClearMunicipalWithholdingsRemovesTheBillingRow()
    {
        var quotation = QuotationWithReteIcaBogota();
        UseOwnBilling(quotation, AccountBogota, Context(Bogota, ReteIcaBogota, agent: true));
        quotation.SetBillingWithholdings([Retefuente, ReteIcaBogota], AdvisorId, Now);
        quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);
        Assert.Equal(3_300m, quotation.RetentionAmount);
        var version = quotation.Version;

        var removed = quotation.ClearMunicipalWithholdings(["ReteICA"], AdvisorId, Now);

        Assert.Equal(0.8m, Assert.Single(removed).Percentage);
        Assert.Equal([Retefuente], quotation.BillingWithholdings.Select(w => w.Rate));
        Assert.Equal(2_500m, quotation.RetentionAmount);
        Assert.Equal(version + 1, quotation.Version);

        Assert.Empty(quotation.ClearMunicipalWithholdings(["ReteICA"], AdvisorId, Now));
        Assert.Equal(version + 1, quotation.Version);

        // Con los datos del cliente vigentes, la marca no toca la fuente del cliente.
        UseClientData(quotation, AccountBogota, Context(Bogota, ReteIcaBogota, agent: true));
        quotation.ClearMunicipalWithholdings(["ReteICA"], AdvisorId, Now);
        Assert.Equal([Retefuente, ReteIcaBogota], quotation.CustomerWithholdings.Select(w => w.Rate));
    }

    [Fact]
    public void ClearMunicipalWithholdingsReturnsOnlyEditedRows()
    {
        var quotation = QuotationWithReteIcaBogota();
        UseOwnBilling(quotation, AccountBogota, Context(Bogota, ReteIcaBogota, agent: true));
        quotation.SetBillingWithholdings([ReteIcaBogota], AdvisorId, Now);

        Assert.Empty(quotation.ClearMunicipalWithholdings(["ReteICA"], AdvisorId, Now));
        Assert.Empty(quotation.BillingWithholdings);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.8125")]
    [InlineData("100.001")]
    public void SetWithholdingPercentageRejectsAnInvalidValue(string value)
    {
        var quotation = QuotationWithReteIcaBogota();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetWithholdingPercentage("ReteICA", decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_percentage_invalid", error.Code);
    }

    [Fact]
    public void SetWithholdingPercentageAcceptsOneHundred()
    {
        var quotation = QuotationWithReteIcaBogota();

        var change = quotation.SetWithholdingPercentage("ReteICA", 100m, AdvisorId, Now);

        Assert.NotNull(change);
        Assert.Equal(100m, quotation.CustomerWithholdings.Single(w => w.Type == "ReteICA").Percentage);
    }

    [Fact]
    public void SetWithholdingPercentageOnANationalTypeIsNotEditable()
    {
        var quotation = QuotationWithReteIcaBogota();

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetWithholdingPercentage("Retefuente", 2m, AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_percentage_not_editable", error.Code);
    }

    [Fact]
    public void SetWithholdingPercentageWithoutARowIsNotEditable()
    {
        var quotation = NewQuotation(AccountBogota, Context(Bogota, ReteIcaBogota, agent: false));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_percentage_not_editable", error.Code);
    }

    [Fact]
    public void SetWithholdingPercentageEditsAndRestoresTheActiveRow()
    {
        var quotation = QuotationWithReteIcaBogota();
        var version = quotation.Version;

        var edited = quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);

        Assert.Equal(new WithholdingPercentageChange("ReteICA Bogotá", 0.966m, 0.8m, Restored: false), edited);
        var row = quotation.CustomerWithholdings.Single(w => w.Type == "ReteICA");
        Assert.True(row.Overridden);
        Assert.Equal(0.966m, row.CatalogPercentage);
        // Base 100.000 × 0,8 % = 800; más 2.500 de Retefuente.
        Assert.Equal(800m, row.Amount);
        Assert.Equal(3_300m, quotation.RetentionAmount);
        Assert.Equal(quotation.Total - 3_300m, quotation.NetTotal);
        Assert.Equal(version + 1, quotation.Version);

        Assert.Null(quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now));
        Assert.Equal(version + 1, quotation.Version);

        var restored = quotation.SetWithholdingPercentage("ReteICA", null, AdvisorId, Now);

        Assert.Equal(new WithholdingPercentageChange("ReteICA Bogotá", 0.8m, 0.966m, Restored: true), restored);
        Assert.False(quotation.CustomerWithholdings.Single(w => w.Type == "ReteICA").Overridden);
        Assert.Equal(3_466m, quotation.RetentionAmount);
    }

    [Fact]
    public void SetWithholdingPercentageOnLargeBaseUsesTheEditedPercentage()
    {
        var quotation = NewQuotation(AccountBogota, Context(Bogota, ReteIcaBogota, agent: true), customerWithholdings: []);
        // 1.190.000 con IVA: base sin IVA 1.000.000.
        AddLine(quotation, unitPrice: 1_190_000m);

        quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now);

        Assert.Equal(8_000m, Assert.Single(quotation.AppliedWithholdings).Amount);
        Assert.Equal(quotation.Total - 8_000m, quotation.NetTotal);
    }

    [Fact]
    public void SetWithholdingPercentageWorksOnASentQuotationButNotOnAConvertedOne()
    {
        var quotation = QuotationWithReteIcaBogota();
        quotation.Send(AdvisorId, Now, QuotationProductSnapshot.None);

        Assert.NotNull(quotation.SetWithholdingPercentage("ReteICA", 0.8m, AdvisorId, Now));

        quotation.ConvertToOrder(AdvisorId, Now, QuotationProductSnapshot.None);

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetWithholdingPercentage("ReteICA", 0.5m, AdvisorId, Now));
        Assert.Equal("quotation.quotation.not_editable", error.Code);
    }

    [Fact]
    public void SelectorsOfAnotherCityAre422()
    {
        var quotation = QuotationWithReteIcaBogota();
        UseOwnBilling(quotation, AccountBogota, Context(Bogota, ReteIcaBogota, agent: true));

        var error = Assert.Throws<QuotationsDomainException>(() =>
            quotation.SetBillingWithholdings([ReteIcaMedellin], AdvisorId, Now));

        Assert.Equal("quotation.billing.withholding_tax_rate_invalid", error.Code);
    }
}
