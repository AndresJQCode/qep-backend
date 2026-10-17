using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// «¿Quién paga el envío?» por tenant (spec 2026-10-17 §4.4): el validador del cuerpo y el caso de uso que
/// guarda las opciones activas, audita sólo si cambian y devuelve la pantalla entera con las tres.
/// </summary>
public sealed class SetFreightChargeModesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private readonly SetFreightChargeModesValidator _validator = new();

    [Fact]
    public void AnEmptyListIsValid()
    {
        Assert.True(_validator.Validate(new SetFreightChargeModesCommand(TenantId, [])).IsValid);
    }

    // Una lista que repite un modo dice lo mismo que sin repetirlo: es un conjunto, no un orden.
    [Fact]
    public void DuplicatesAreTolerated()
    {
        Assert.True(_validator.Validate(new SetFreightChargeModesCommand(TenantId, ["Free", "Free", "Charged"])).IsValid);
    }

    [Theory]
    [InlineData("Gratis")]
    [InlineData("free")]
    [InlineData("2")]
    [InlineData(null)]
    public void AnUnknownModeIsAFieldError(string? mode)
    {
        var result = _validator.Validate(new SetFreightChargeModesCommand(TenantId, ["Charged", mode]));

        var error = Assert.Single(result.Errors);
        Assert.Equal("EnabledModes[1]", error.PropertyName);
    }

    [Fact]
    public void AMissingListIsAFieldError()
    {
        var result = _validator.Validate(new SetFreightChargeModesCommand(TenantId, null));

        var error = Assert.Single(result.Errors);
        Assert.Equal("EnabledModes", error.PropertyName);
    }

    [Fact]
    public async Task SavesTheEnabledModesAndReturnsTheThreeInOrder()
    {
        var (handler, store, audit) = Handler();

        var settings = await handler.HandleAsync(
            new SetFreightChargeModesCommand(TenantId, ["Free", "Charged", "Free"]), TestContext.Current.CancellationToken);

        Assert.Equal([FreightChargeMode.Charged, FreightChargeMode.Free], store.EnabledFreightChargeModes.Order());
        Assert.Equal(
            [new("Charged", true), new("OnDelivery", false), new FreightChargeModeDto("Free", true)],
            settings.FreightChargeModes);
        Assert.Equal([SetFreightChargeModesHandler.AuditAction], audit.Actions);
    }

    // Lo mismo que ya estaba no es un cambio: no se guarda ni se audita, como la tasa del envío.
    [Fact]
    public async Task TheSameModesAreNotAudited()
    {
        var (handler, _, audit) = Handler();

        var settings = await handler.HandleAsync(
            new SetFreightChargeModesCommand(TenantId, ["OnDelivery", "Free", "Charged"]),
            TestContext.Current.CancellationToken);

        Assert.All(settings.FreightChargeModes, mode => Assert.True(mode.IsEnabled));
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task AnotherTenantIsForbidden()
    {
        var (handler, _, _) = Handler(new StubExecutionContext(Guid.CreateVersion7(), Guid.CreateVersion7()));

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            handler.HandleAsync(new SetFreightChargeModesCommand(TenantId, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnInvalidBodyIsAValidationError()
    {
        var (handler, _, _) = Handler();

        await Assert.ThrowsAsync<ValidationException>(() =>
            handler.HandleAsync(new SetFreightChargeModesCommand(TenantId, ["Paid"]), TestContext.Current.CancellationToken));
    }

    private static (SetFreightChargeModesHandler Handler, FixedQuotationSettingsStore Store, RecordingQuotationAuditPublisher Audit)
        Handler(IExecutionContext? executionContext = null)
    {
        var store = new FixedQuotationSettingsStore();
        var audit = new RecordingQuotationAuditPublisher();
        var handler = new SetFreightChargeModesHandler(
            new StubCarrierRepository(),
            store,
            new StubQuotationTaxRateLookup(null),
            FixedTenantModules.Simulated,
            new NoOpQuotationsUnitOfWork(),
            audit,
            executionContext ?? new StubExecutionContext(Guid.CreateVersion7(), TenantId),
            new FixedClock(Now),
            new SetFreightChargeModesValidator());
        return (handler, store, audit);
    }
}
