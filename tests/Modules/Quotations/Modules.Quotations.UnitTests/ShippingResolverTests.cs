using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Lo que el dominio no puede saber del envío (spec 2026-10-17 §5.2, P3): si la transportadora existe, está
/// activa y hace recaudo, y cuál es la tasa del tenant. La coherencia (recogida, montos) la valida el
/// agregado, así que estas pruebas no la repiten.
/// </summary>
public sealed class ShippingResolverTests
{
    private const string Prefix = "quotation.quotation";
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 15, 0, 0, TimeSpan.Zero);

    private static Carrier NewCarrier(string name = "Coordinadora", bool supportsCashOnDelivery = true) =>
        Carrier.Create(CarrierId.New(), TenantId, name, [], supportsCashOnDelivery, 0, Now);

    private static (ShippingResolver Resolver, StubCarrierRepository Carriers) Resolver(
        Carrier[]? carriers = null,
        QuotationTaxRateRef? rate = null,
        bool taxRateMustNotBeRead = false,
        bool configureRate = true)
    {
        var repository = new StubCarrierRepository(carriers ?? []);
        var settings = new FixedQuotationSettingsStore
        {
            FreightTaxRateId = configureRate ? rate?.Id : null,
        };
        var lookup = new StubQuotationTaxRateLookup(rate, failIfCalled: taxRateMustNotBeRead);
        return (new ShippingResolver(repository, lookup, settings), repository);
    }

    private static Task<ShippingDetails?> ResolveAsync(
        ShippingResolver resolver,
        ShippingRequest? request,
        ShippingDetails? current = null,
        bool isStorePickup = false,
        string currency = "COP") =>
        resolver.ResolveAsync(
            TenantId, request, current ?? ShippingDetails.None, isStorePickup, currency, Prefix,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task PickupClearsEverything()
    {
        var (resolver, _) = Resolver();
        var current = new ShippingDetails(Guid.CreateVersion7(), "Coordinadora", FreightChargeMode.Charged, 15000m, 19, true);

        var withShipping = await ResolveAsync(
            resolver, new ShippingRequest(null, "Charged", 15000m, false), current, isStorePickup: true);
        var alreadyClear = await ResolveAsync(resolver, null, ShippingDetails.None, isStorePickup: true);

        Assert.Equal(ShippingDetails.None, withShipping);
        Assert.Null(alreadyClear);
    }

    [Fact]
    public async Task AbsentRequestLeavesItAlone()
    {
        var (resolver, _) = Resolver();
        var current = new ShippingDetails(Guid.CreateVersion7(), "Coordinadora", FreightChargeMode.Free, 0m, 0, false);

        Assert.Null(await ResolveAsync(resolver, null, current));
    }

    // Review Focus 2: un documento que ya tiene una transportadora desactivada la conserva al guardarse.
    [Fact]
    public async Task KeepsADeactivatedCarrierWhenItDidNotChange()
    {
        var carrier = NewCarrier();
        carrier.Deactivate(Now);
        var (resolver, carriers) = Resolver([carrier]);
        var current = new ShippingDetails(carrier.Id.Value, "Coordinadora", null, 0m, 0, false);

        var resolved = await ResolveAsync(resolver, new ShippingRequest(carrier.Id.Value, null, 0m, false), current);

        Assert.NotNull(resolved);
        Assert.Equal(carrier.Id.Value, resolved.CarrierId);
        Assert.Equal("Coordinadora", resolved.CarrierName);
        Assert.Equal(0, carriers.FindCalls);
    }

    [Fact]
    public async Task RejectsAnInactiveCarrierWhenChosen()
    {
        var carrier = NewCarrier();
        carrier.Deactivate(Now);
        var (resolver, _) = Resolver([carrier]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ResolveAsync(resolver, new ShippingRequest(carrier.Id.Value, null, 0m, false)));

        Assert.Equal("quotation.quotation.carrier_not_available", error.Code);
    }

    [Fact]
    public async Task RejectsAnUnknownCarrier()
    {
        var (resolver, _) = Resolver();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ResolveAsync(resolver, new ShippingRequest(Guid.CreateVersion7(), null, 0m, false)));

        Assert.Equal("quotation.quotation.carrier_not_available", error.Code);
    }

    [Fact]
    public async Task RejectsCashOnDeliveryWithACarrierThatDoesNotCollect()
    {
        var carrier = NewCarrier("Servientrega", supportsCashOnDelivery: false);
        var (resolver, _) = Resolver([carrier]);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ResolveAsync(resolver, new ShippingRequest(carrier.Id.Value, null, 0m, true)));

        Assert.Equal("quotation.quotation.cash_on_delivery_not_supported", error.Code);
    }

    // Prender la contraentrega con la transportadora que ya estaba también se comprueba.
    [Fact]
    public async Task RejectsTurningCashOnDeliveryOnWithTheSameCarrierThatDoesNotCollect()
    {
        var carrier = NewCarrier("Servientrega", supportsCashOnDelivery: false);
        var (resolver, _) = Resolver([carrier]);
        var current = new ShippingDetails(carrier.Id.Value, "Servientrega", null, 0m, 0, false);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ResolveAsync(resolver, new ShippingRequest(carrier.Id.Value, null, 0m, true), current));

        Assert.Equal("quotation.quotation.cash_on_delivery_not_supported", error.Code);
    }

    // Si la transportadora dejó de hacer recaudo después, el documento conserva lo elegido (spec §5.2).
    [Fact]
    public async Task KeepsCashOnDeliveryAlreadyChosenWithTheSameCarrier()
    {
        var carrier = NewCarrier("Servientrega", supportsCashOnDelivery: false);
        var (resolver, carriers) = Resolver([carrier]);
        var current = new ShippingDetails(carrier.Id.Value, "Servientrega", null, 0m, 0, true);

        var resolved = await ResolveAsync(resolver, new ShippingRequest(carrier.Id.Value, null, 0m, true), current);

        Assert.NotNull(resolved);
        Assert.True(resolved.CashOnDelivery);
        Assert.Equal(0, carriers.FindCalls);
    }

    [Fact]
    public async Task AcceptsCashOnDeliveryWithoutCarrier()
    {
        var (resolver, _) = Resolver();

        var resolved = await ResolveAsync(resolver, new ShippingRequest(null, null, 0m, true));

        Assert.NotNull(resolved);
        Assert.True(resolved.CashOnDelivery);
        Assert.Null(resolved.CarrierId);
        Assert.Null(resolved.CarrierName);
    }

    [Fact]
    public async Task ChargedReadsTheTenantTaxRate()
    {
        var carrier = NewCarrier();
        var rate = new QuotationTaxRateRef(Guid.CreateVersion7(), "IVA 19", 19, true);
        var (resolver, _) = Resolver([carrier], rate);

        var resolved = await ResolveAsync(resolver, new ShippingRequest(carrier.Id.Value, "Charged", 15000m, false));

        Assert.NotNull(resolved);
        Assert.Equal(FreightChargeMode.Charged, resolved.ChargeMode);
        Assert.Equal(15000m, resolved.FreightAmount);
        Assert.Equal(19, resolved.FreightTaxPercentage);
        Assert.Equal("Coordinadora", resolved.CarrierName);
    }

    // Review Focus 1: un guardado que no tocó el envío conserva la foto de la tasa, aunque la del tenant
    // haya cambiado o se haya borrado.
    [Fact]
    public async Task SavingWithUnchangedFreightDoesNotRereadTheTaxRate()
    {
        var (resolver, _) = Resolver(rate: null, taxRateMustNotBeRead: true);
        var current = new ShippingDetails(null, null, FreightChargeMode.Charged, 15000m, 16, false);

        var resolved = await ResolveAsync(resolver, new ShippingRequest(null, "Charged", 15000m, false), current);

        Assert.NotNull(resolved);
        Assert.Equal(16, resolved.FreightTaxPercentage);
    }

    [Fact]
    public async Task ChargedWithoutTaxRateIsRejected()
    {
        var (resolver, _) = Resolver();

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ResolveAsync(resolver, new ShippingRequest(null, "Charged", 15000m, false)));

        Assert.Equal("quotation.quotation.freight_tax_rate_missing", error.Code);
    }

    [Fact]
    public async Task ChargedWithAnInactiveTaxRateIsRejected()
    {
        var (resolver, _) = Resolver(rate: new QuotationTaxRateRef(Guid.CreateVersion7(), "IVA 19", 19, false));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            ResolveAsync(resolver, new ShippingRequest(null, "Charged", 15000m, false)));

        Assert.Equal("quotation.quotation.freight_tax_rate_missing", error.Code);
    }

    [Fact]
    public async Task ChargedInUsdNeedsNoTaxRate()
    {
        var (resolver, _) = Resolver(taxRateMustNotBeRead: true);

        var resolved = await ResolveAsync(
            resolver, new ShippingRequest(null, "Charged", 25.50m, false), currency: "USD");

        Assert.NotNull(resolved);
        Assert.Equal(25.50m, resolved.FreightAmount);
        Assert.Equal(0, resolved.FreightTaxPercentage);
    }

    [Fact]
    public async Task FreeForcesTheAmountToZero()
    {
        var (resolver, _) = Resolver(taxRateMustNotBeRead: true);

        var resolved = await ResolveAsync(resolver, new ShippingRequest(null, "Free", 15000m, false));

        Assert.NotNull(resolved);
        Assert.Equal(FreightChargeMode.Free, resolved.ChargeMode);
        Assert.Equal(0m, resolved.FreightAmount);
    }
}
