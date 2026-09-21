using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Lo que la cotización le cuenta al frontend sobre la compra mínima. Sin esto la pantalla ve un
/// descuento en cero y no tiene con qué explicarlo — mismo motivo por el que viajan
/// <c>CustomerVatSurplus</c> y <c>HasChangesSinceSent</c>.
/// </summary>
public sealed class QuotationMinimumPurchaseDtoTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ClientId = Guid.CreateVersion7();
    private static readonly MemberId AdvisorId = new(Guid.CreateVersion7());
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static Quotation NewQuotation(string currency = "COP") =>
        Quotation.Create(
            QuotationId.New(),
            TenantId,
            "QUO-2026-0001",
            ClientId,
            AdvisorId,
            new DateOnly(2026, 10, 31),
            paymentMethod: "Transferencia bancaria",
            notes: null,
            QuotationParties.Empty,
            new QuotationBillingAccount
            {
                CompanyId = Guid.CreateVersion7(),
                BankName = "Bancolombia",
                AccountNumber = "12345678",
                Currency = currency
            },
            customerWithRetention: false,
            customerVatSurplus: false,
            AdvisorId,
            Now);

    private static void AddItem(Quotation quotation, decimal quantity, decimal unitPrice) =>
        quotation.AddItem(
            QuotationItemId.New(), Guid.CreateVersion7(), quantity, unitPrice, 0m, 19, AdvisorId, Now);

    [Fact]
    public void SixUnitsReportTheMinimumAsMetWithNothingMissing()
    {
        var quotation = NewQuotation();
        AddItem(quotation, 6m, 1_000m);

        var minimum = quotation.ToDto().MinimumPurchase;

        Assert.True(minimum.Met);
        Assert.Equal(6m, minimum.Units);
        Assert.Equal(0m, minimum.MissingUnits);
        Assert.Equal(0m, minimum.MissingTotal);
    }

    // Lo que la pantalla necesita para escribir "te faltan 4 unidades o $480.000": los dos
    // faltantes calculados acá y no restados por el cliente, que es como se evita que cada
    // pantalla redondee distinto.
    [Fact]
    public void BelowBothMinimumsReportsHowMuchIsMissingOnEachBranch()
    {
        var quotation = NewQuotation();
        AddItem(quotation, 2m, 10_000m);

        var minimum = quotation.ToDto().MinimumPurchase;

        Assert.False(minimum.Met);
        Assert.Equal(2m, minimum.Units);
        Assert.Equal(6m, minimum.MinimumUnits);
        Assert.Equal(500_000m, minimum.MinimumTotal);
        Assert.Equal(4m, minimum.MissingUnits);
        Assert.Equal(480_000m, minimum.MissingTotal);
    }

    // El umbral viaja en la moneda de la cotización: la pantalla no lo convierte ni lo sabe de
    // memoria.
    [Fact]
    public void TheUsdQuotationReportsTheUsdThreshold()
    {
        var quotation = NewQuotation(currency: "USD");
        AddItem(quotation, 2m, 50m);

        var minimum = quotation.ToDto().MinimumPurchase;

        Assert.False(minimum.Met);
        Assert.Equal(200m, minimum.MinimumTotal);
        Assert.Equal(100m, minimum.MissingTotal);
    }

    // Una cotización vacía no alcanza nada, y el bloque viaja igual: un campo ausente obligaría a
    // la pantalla a inventar el caso.
    [Fact]
    public void AnEmptyQuotationStillReportsTheWholeBlock()
    {
        var minimum = NewQuotation().ToDto().MinimumPurchase;

        Assert.False(minimum.Met);
        Assert.Equal(0m, minimum.Units);
        Assert.Equal(6m, minimum.MissingUnits);
        Assert.Equal(500_000m, minimum.MissingTotal);
    }

    // Con el mínimo alcanzado por plata y no por cantidad, el faltante de unidades también es 0:
    // es un OR, y una sola rama cumplida cierra las dos.
    [Fact]
    public void MeetingTheAmountBranchLeavesNoUnitsMissing()
    {
        var quotation = NewQuotation();
        AddItem(quotation, 2m, 300_000m);

        var minimum = quotation.ToDto().MinimumPurchase;

        Assert.True(minimum.Met);
        Assert.Equal(0m, minimum.MissingUnits);
        Assert.Equal(0m, minimum.MissingTotal);
    }
}
