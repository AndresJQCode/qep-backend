using Modules.Shopify.Domain;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// Spec 2026-11-17 (onboarding) §4 y §7, plan Tarea 4: el estado del asistente vive en la tienda. La cobertura medida, la
/// aceptada y el resumen del historial no son configuración del asesor: no suben <see cref="ShopifyStore.Version"/>, o el
/// formulario abierto recibiría 412 (Review Focus 2). Asignar la empresa sí es configuración y sí la sube.
/// </summary>
public sealed class ShopifyStoreSetupTests
{
    private static readonly DateTimeOffset LinkedAt = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);

    private static ShopifyStore NewStore() => ShopifyStore.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), LinkedAt);

    private static ShopifyProductsCoverage Coverage(int variants, int matched) =>
        new(variants, matched, CatalogEmpty: false, [new ShopifyCoverageSample("SKU-1", "Camiseta")], LinkedAt);

    [Fact]
    public void RecordingCoverageDoesNotBumpTheVersion()
    {
        var store = NewStore();
        var coverage = Coverage(10, 4);

        store.RecordProductsCoverage(coverage);

        Assert.Same(coverage, store.ProductsCoverage);
        Assert.Equal(1L, store.Version);
        Assert.Equal(LinkedAt, store.UpdatedAt);
    }

    [Fact]
    public void AcceptingTwiceKeepsTheFirstInstant()
    {
        var store = NewStore();

        store.AcceptProductsCoverage(LinkedAt.AddMinutes(5));
        store.AcceptProductsCoverage(LinkedAt.AddMinutes(9));

        Assert.Equal(LinkedAt.AddMinutes(5), store.ProductsCoverageAcceptedAt);
        Assert.Equal(1L, store.Version);
    }

    [Fact]
    public void RecordingTheSetupHistoryKeepsItsInstantWithoutBumpingTheVersion()
    {
        var store = NewStore();
        var summary = new ShopifySetupHistorySummary(
            LinkedAt.AddHours(1), 7, 12, 9, [new ShopifyWaitingCount("product_unmatched", 3)]);

        store.RecordSetupHistory(summary);

        Assert.Same(summary, store.SetupHistorySummary);
        Assert.Equal(LinkedAt.AddHours(1), store.SetupHistoryRanAt);
        Assert.Equal(1L, store.Version);
    }

    // Spec §4.2: «Empresa» asigna sólo la empresa (y la cuenta); el asesor y lo demás que ya estaba no se tocan.
    [Fact]
    public void AssignBillingBumpsTheVersionAndKeepsTheAdvisor()
    {
        var store = NewStore();
        var advisor = Guid.CreateVersion7();
        store.Configure(
            advisor, new ShopifyBillingAccount(Guid.CreateVersion7(), null, null, "COP"), null, "Documento", "Tipo de documento", "CC",
            7, ShopifyEditSync.Manual, ["PaymentPending"], Guid.CreateVersion7(), LinkedAt);
        var companyId = Guid.CreateVersion7();
        var updatedBy = Guid.CreateVersion7();
        var now = LinkedAt.AddHours(2);

        store.AssignBilling(new ShopifyBillingAccount(companyId, " Bancolombia ", "123-456", "cop"), updatedBy, now);

        Assert.Equal(3L, store.Version);
        Assert.Equal(advisor, store.AdvisorMembershipId);
        Assert.Equal(7, store.HistoryDays);
        Assert.Equal(new ShopifyBillingAccount(companyId, "Bancolombia", "123-456", "COP"), store.Billing);
        Assert.Equal((updatedBy, now), (store.UpdatedBy!.Value, store.UpdatedAt));
    }

    [Fact]
    public void AssignBillingWithoutAnAccountKeepsTheCompanyOnly()
    {
        var store = NewStore();
        var companyId = Guid.CreateVersion7();

        store.AssignBilling(new ShopifyBillingAccount(companyId, " ", "", "COP"), Guid.CreateVersion7(), LinkedAt);

        Assert.Equal(new ShopifyBillingAccount(companyId, null, null, "COP"), store.Billing);
        Assert.Equal(2L, store.Version);
        // Sin asesor todavía: no está configurada.
        Assert.False(store.IsConfigured);
    }

    [Fact]
    public void AssignBillingWithOnlyTheAccountNumberThrows()
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() =>
            store.AssignBilling(new ShopifyBillingAccount(Guid.CreateVersion7(), null, "123-456", "COP"), Guid.CreateVersion7(), LinkedAt));
        Assert.Null(store.Billing);
        Assert.Equal(1L, store.Version);
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 4, true)]
    [InlineData(3, 2, false)]
    [InlineData(0, 0, false)]
    public void ACoverageWithEveryVariantMatchedIsComplete(int variants, int matched, bool complete)
    {
        Assert.Equal(complete, Coverage(variants, matched).IsComplete);
    }
}
