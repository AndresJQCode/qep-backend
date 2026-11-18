using BuildingBlocks.Domain.Catalog;
using BuildingBlocks.Domain.Taxes;
using Modules.Quotations.Domain;
using static Modules.Quotations.UnitTests.TestWithholdings;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El veredicto de cada tasa de la fuente vigente (spec retenciones por UVT §5.1, §5.3): si aplica y por qué, con la misma
/// regla que decide qué entra al cálculo. <c>For</c> es <c>Explain</c> filtrado, nunca una copia.
/// </summary>
public sealed class WithholdingVerdictTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Bogota = Guid.Parse("01900000-0000-7000-8000-0000000b0901");
    private static readonly Guid Medellin = Guid.Parse("01900000-0000-7000-8000-0000000e0d01");

    private static readonly QuotationBillingAccount ColombianAccount = new()
    {
        CompanyId = Guid.CreateVersion7(),
        BankName = "Bancolombia",
        AccountNumber = "12345678",
        Currency = "COP",
        Country = "CO",
    };

    // 4 UVT x 49.799 = 199.196.
    private static readonly WithholdingRate RetefuenteServicios =
        new(Guid.CreateVersion7(), "Retefuente servicios 4 %", "Retefuente", "CO", 4m, 4m, "UVT", 49_799m)
        {
            TaxUnitYear = 2025,
        };

    private static readonly WithholdingRate WithoutTaxUnit = RetefuenteServicios with { TaxUnitValue = null, TaxUnitYear = null };

    private static readonly WithholdingRate ReteIcaBogota = Rate("ReteICA Bogotá", "ReteICA", 0.966m, Bogota);

    private static readonly WithholdingRate ReteIcaMedellin = Rate("ReteICA Medellín", "ReteICA", 1m, Medellin);

    private static readonly WithholdingRate RetefuenteMexico =
        new(Guid.CreateVersion7(), "Retención ISR 10 %", "Retefuente", "MX", 10m, null, null, null);

    public static TheoryData<string, WithholdingReason> Cases => new()
    {
        { "final consumer", WithholdingReason.FinalConsumer },
        { "no billing company", WithholdingReason.NoBillingCompany },
        { "usd", WithholdingReason.CurrencyWithoutTax },
        { "other municipality", WithholdingReason.OtherMunicipality },
        { "no tax unit value", WithholdingReason.NoTaxUnitValue },
        { "below minimum base", WithholdingReason.BelowMinimumBase },
        { "equal to the threshold", WithholdingReason.Applied },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EachReason(string scenario, WithholdingReason expected)
    {
        var (rate, finalConsumer, account, currency, subtotal) = scenario switch
        {
            "final consumer" => (RetefuenteServicios, true, ColombianAccount, "COP", 199_196m),
            "no billing company" => (RetefuenteServicios, false, (QuotationBillingAccount?)null, "COP", 199_196m),
            "usd" => (RetefuenteServicios, false, ColombianAccount with { Currency = "USD" }, "USD", 199_196m),
            "other municipality" => (ReteIcaMedellin, false, ColombianAccount, "COP", 199_196m),
            "no tax unit value" => (WithoutTaxUnit, false, ColombianAccount, "COP", 199_196m),
            "below minimum base" => (RetefuenteServicios, false, ColombianAccount, "COP", 150_000m),
            "equal to the threshold" => (RetefuenteServicios, false, ColombianAccount, "COP", 199_196m),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var verdict = Assert.Single(
            WithholdingCandidates.Explain([rate], finalConsumer, account, currency, Bogota, GoodsOnly(subtotal)));

        Assert.Equal((rate, expected, subtotal), (verdict.Rate, verdict.Reason, verdict.Base));
        Assert.Equal(expected == WithholdingReason.Applied, verdict.Applies);
        Assert.Equal(expected == WithholdingReason.Applied ? 7_967.84m : 0m, verdict.Amount);
    }

    [Fact]
    public void MinimumBaseAmountIsTheThresholdOnlyWithAMinimumBase()
    {
        var verdicts = WithholdingCandidates.Explain(
            [RetefuenteServicios, WithoutTaxUnit, RetefuenteCo], false, ColombianAccount, "COP", Bogota, GoodsOnly(100_000m));

        Assert.Equal([199_196m, null, null], verdicts.Select(verdict => verdict.MinimumBaseAmount));
    }

    [Fact]
    public void FinalConsumerWinsOverThreshold()
    {
        var verdict = Assert.Single(
            WithholdingCandidates.Explain([RetefuenteServicios], true, ColombianAccount, "COP", Bogota, GoodsOnly(500_000m)));

        Assert.Equal(WithholdingReason.FinalConsumer, verdict.Reason);
    }

    [Fact]
    public void OtherCountryRatesAreLeftOut()
    {
        var verdicts = WithholdingCandidates.Explain(
            [RetefuenteMexico, RetefuenteCo], false, ColombianAccount, "COP", Bogota, GoodsOnly(100_000m));

        Assert.Equal(RetefuenteCo, Assert.Single(verdicts).Rate);
    }

    public static TheoryData<Guid?, WithholdingRate[]> ForCases => new()
    {
        { Medellin, [RetefuenteCo] },
        { Bogota, [ReteIcaBogota, RetefuenteCo] },
        { null, [RetefuenteCo] },
    };

    // Los tres casos de MunicipalWithholdingsTests: For da lo de siempre y es Explain sin los motivos de los pasos 1–4.
    [Theory]
    [MemberData(nameof(ForCases))]
    public void ForEqualsExplainFiltered(Guid? operationCityId, WithholdingRate[] expected)
    {
        WithholdingRate[] source = operationCityId is null ? [RetefuenteCo] : [ReteIcaBogota, RetefuenteCo];

        var candidates = WithholdingCandidates.For(source, false, ColombianAccount, "COP", operationCityId);
        var explained = WithholdingCandidates.Explain(source, false, ColombianAccount, "COP", operationCityId, GoodsOnly(0m))
            .Where(verdict => verdict.Reason is WithholdingReason.Applied
                or WithholdingReason.BelowMinimumBase
                or WithholdingReason.NoTaxUnitValue)
            .Select(verdict => verdict.Rate);

        Assert.Equal(expected, candidates);
        Assert.Equal(expected, explained);
    }

    [Fact]
    public void QuotationVerdictsFollowTheActiveSource()
    {
        var quotation = NewQuotation([RetefuenteCo], subtotalWithVat: 119_000m);
        quotation.UpdateDetails(
            new DateOnly(2026, 12, 31), null, null, new QuotationParties(OwnBilling, null, BillingVatSurplus: false),
            ColombianAccount, null, isRetail: false, globalScaleFloor: null, MunicipalContext.None, AdvisorId, Now);
        quotation.SetBillingWithholdings([RetefuenteServicios], AdvisorId, Now);

        var verdict = Assert.Single(quotation.WithholdingVerdicts);

        Assert.Equal((RetefuenteServicios, WithholdingReason.BelowMinimumBase), (verdict.Rate, verdict.Reason));
    }

    // Review Focus 1: dos tasas, una aplica y otra no. El desglose trae las dos en el orden de la fuente y las aplicadas
    // sólo la que aplicó.
    [Fact]
    public void VerdictsKeepSourceOrder()
    {
        var quotation = NewQuotation([RetefuenteServicios, RetefuenteCo], subtotalWithVat: 119_000m);

        Assert.Equal(
            [
                (RetefuenteServicios, WithholdingReason.BelowMinimumBase, 100_000m, 0m),
                (RetefuenteCo, WithholdingReason.Applied, 100_000m, 2_500m),
            ],
            quotation.WithholdingVerdicts.Select(verdict => (verdict.Rate, verdict.Reason, verdict.Base, verdict.Amount)));
        Assert.Equal(RetefuenteCo, Assert.Single(quotation.AppliedWithholdings).Rate);
    }

    [Fact]
    public void OrderVerdictsComeFromItsRows()
    {
        var order = Order.CreateManual(
            OrderId.New(), TenantId, "PED-2026-0001",
            new OrderHeader(ClientId, AdvisorId, "COP", null, IsRetail: false, null, CustomerVatSurplus: false, [RetefuenteServicios, RetefuenteCo]),
            QuotationParties.Empty, ColombianAccount, ShippingDetails.None,
            [new OrderLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 119_000m, 0m, 19, new QuotationProductSnapshot("ABC", "Aceite"))],
            null, AdvisorId, [], Now, orderDate: DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal(
            [(WithholdingReason.BelowMinimumBase, 0m), (WithholdingReason.Applied, 2_500m)],
            order.WithholdingVerdicts.Select(verdict => (verdict.Reason, verdict.Amount)));
    }

    // Spec 2026-11-18 §4.2: un bien de 1.000.000 y un servicio de 500.000, sin IVA.
    private static readonly WithholdingBases Mixed = WithholdingBases.From(
    [
        new LineAmountsView(1_000_000m, 0m, 190_000m, ProductKind.Physical),
        new LineAmountsView(500_000m, 0m, 95_000m, ProductKind.Service),
    ]);

    private static readonly WithholdingBases ServicesOnly =
        WithholdingBases.From([new LineAmountsView(500_000m, 0m, 95_000m, ProductKind.Service)]);

    private static readonly WithholdingRate GoodsRate = RetefuenteCo with { AppliesTo = WithholdingScope.Goods };

    private static readonly WithholdingRate ServicesRate =
        new(Guid.CreateVersion7(), "Retefuente servicios 4 %", "Retefuente", "CO", 4m, null, null, null)
        {
            AppliesTo = WithholdingScope.Services,
        };

    [Fact]
    public void MixedDocumentWithholdsEachRateOnItsScopeBase()
    {
        var verdicts = WithholdingCandidates.Explain([GoodsRate, ServicesRate], false, ColombianAccount, "COP", Bogota, Mixed);

        Assert.Equal(
            [
                (GoodsRate, WithholdingReason.Applied, 1_000_000m, 25_000m),
                (ServicesRate, WithholdingReason.Applied, 500_000m, 20_000m),
            ],
            verdicts.Select(verdict => (verdict.Rate, verdict.Reason, verdict.Base, verdict.Amount)));
    }

    [Fact]
    public void AllScopeRatesReproduceTodaysTotals()
    {
        var verdict = Assert.Single(
            WithholdingCandidates.Explain([RetefuenteCo], false, ColombianAccount, "COP", Bogota, Mixed));

        Assert.Equal((WithholdingReason.Applied, 1_500_000m, 37_500m), (verdict.Reason, verdict.Base, verdict.Amount));
    }

    [Fact]
    public void AGoodsRateOnAServicesOnlyDocumentIsNoLinesOfScope()
    {
        var verdict = Assert.Single(
            WithholdingCandidates.Explain([GoodsRate], false, ColombianAccount, "COP", Bogota, ServicesOnly));

        Assert.Equal((WithholdingReason.NoLinesOfScope, 0m, 0m), (verdict.Reason, verdict.Base, verdict.Amount));
        Assert.False(verdict.Applies);
    }

    // 12 UVT x 50.000 = 600.000: los servicios suman 500.000 y no lo alcanzan, aunque el documento entero sume 1.500.000.
    [Fact]
    public void TheThresholdIsComparedWithTheScopeBase()
    {
        var withBase = ServicesRate with { MinimumBase = 12m, MinimumBaseUnit = "UVT", TaxUnitValue = 50_000m };

        var verdict = Assert.Single(
            WithholdingCandidates.Explain([withBase], false, ColombianAccount, "COP", Bogota, Mixed));

        Assert.Equal(600_000m, withBase.Threshold);
        Assert.Equal((WithholdingReason.BelowMinimumBase, 500_000m, 0m), (verdict.Reason, verdict.Base, verdict.Amount));
    }

    // El orden de los motivos (spec 2026-11-18 §4.2): primero los que descartan la tasa antes del cálculo, después
    // NoLinesOfScope y al final el umbral.
    [Fact]
    public void FinalConsumerStillWinsOverNoLinesOfScope()
    {
        var finalConsumer = Assert.Single(
            WithholdingCandidates.Explain([GoodsRate], true, ColombianAccount, "COP", Bogota, ServicesOnly));
        var noTaxUnit = Assert.Single(WithholdingCandidates.Explain(
            [GoodsRate with { MinimumBase = 4m, MinimumBaseUnit = "UVT", TaxUnitValue = null }], false, ColombianAccount,
            "COP", Bogota, ServicesOnly));

        Assert.Equal(WithholdingReason.FinalConsumer, finalConsumer.Reason);
        Assert.Equal(WithholdingReason.NoLinesOfScope, noTaxUnit.Reason);
    }

    // La vista previa (veredictos) y el total guardado salen del mismo cálculo: nunca difieren.
    [Fact]
    public void QuotationVerdictsMatchThePersistedWithholdingsOnAMixedDocument()
    {
        var quotation = NewQuotation([GoodsRate, ServicesRate], subtotalWithVat: 1_190_000m);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, 595_000m, 0m, 19, AdvisorId, Now,
            ProductKind.Service);

        Assert.Equal(
            [(GoodsRate, 1_000_000m, 25_000m), (ServicesRate, 500_000m, 20_000m)],
            quotation.WithholdingVerdicts.Select(verdict => (verdict.Rate, verdict.Base, verdict.Amount)));
        Assert.Equal(
            [(GoodsRate.TaxRateId, 1_000_000m, 25_000m), (ServicesRate.TaxRateId, 500_000m, 20_000m)],
            quotation.AppliedWithholdings.Select(row => (row.TaxRateId, row.BaseAmount, row.Amount)));
        Assert.Equal(45_000m, quotation.RetentionAmount);
    }

    private static WithholdingBases GoodsOnly(decimal subtotal) => new(subtotal, subtotal, 0m);

    private static readonly QuotationPartyDetails OwnBilling = new()
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

    private static Quotation NewQuotation(IReadOnlyList<WithholdingRate> customerWithholdings, decimal subtotalWithVat)
    {
        var quotation = Quotation.Create(
            QuotationId.New(), TenantId, "COT-2026-0001", ClientId, AdvisorId, new DateOnly(2026, 12, 31), null, null,
            QuotationParties.Empty, ColombianAccount, "COP", customerVatSurplus: false, customerWithholdings,
            MunicipalContext.None, AdvisorId, Now);
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1m, subtotalWithVat, 0m, 19, AdvisorId, Now);
        return quotation;
    }
}
