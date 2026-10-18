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

    // §4.4.1: el por defecto es null o uno de los activos del mismo cuerpo.
    [Theory]
    [InlineData("Free")]
    [InlineData(null)]
    public void ADefaultModeAmongTheEnabledOnesOrNullIsValid(string? defaultMode)
    {
        var command = new SetFreightChargeModesCommand(
            TenantId, ["Charged", "Free"], SetsDefaultMode: true, DefaultMode: defaultMode);

        Assert.True(_validator.Validate(command).IsValid);
    }

    [Theory]
    [InlineData("OnDelivery")]
    [InlineData("free")]
    [InlineData("Gratis")]
    public void ADefaultModeThatIsNotEnabledIsAFieldError(string defaultMode)
    {
        var command = new SetFreightChargeModesCommand(
            TenantId, ["Charged", "Free"], SetsDefaultMode: true, DefaultMode: defaultMode);

        var error = Assert.Single(_validator.Validate(command).Errors);
        Assert.Equal("DefaultMode", error.PropertyName);
    }

    [Fact]
    public async Task SetsTheDefaultModeAndReturnsIt()
    {
        var (handler, store, audit) = Handler();

        var settings = await handler.HandleAsync(
            new SetFreightChargeModesCommand(
                TenantId, ["Charged", "OnDelivery", "Free"], SetsDefaultMode: true, DefaultMode: "OnDelivery"),
            TestContext.Current.CancellationToken);

        Assert.Equal(FreightChargeMode.OnDelivery, store.DefaultFreightChargeMode);
        Assert.Equal("OnDelivery", settings.DefaultFreightChargeMode);
        Assert.Equal([SetFreightChargeModesHandler.AuditAction], audit.Actions);
    }

    [Fact]
    public async Task ANullDefaultModeClearsIt()
    {
        var (handler, store, audit) = Handler();
        store.DefaultFreightChargeMode = FreightChargeMode.Free;

        var settings = await handler.HandleAsync(
            new SetFreightChargeModesCommand(
                TenantId, ["Charged", "OnDelivery", "Free"], SetsDefaultMode: true, DefaultMode: null),
            TestContext.Current.CancellationToken);

        Assert.Null(store.DefaultFreightChargeMode);
        Assert.Null(settings.DefaultFreightChargeMode);
        Assert.Equal([SetFreightChargeModesHandler.AuditAction], audit.Actions);
    }

    // Omitido no es null: conserva el que había. Con los mismos modos no hay nada que guardar ni auditar.
    [Fact]
    public async Task AnOmittedDefaultModeKeepsTheCurrentOne()
    {
        var (handler, store, audit) = Handler();
        store.DefaultFreightChargeMode = FreightChargeMode.Free;

        var settings = await handler.HandleAsync(
            new SetFreightChargeModesCommand(TenantId, ["Charged", "OnDelivery", "Free"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(FreightChargeMode.Free, store.DefaultFreightChargeMode);
        Assert.Equal("Free", settings.DefaultFreightChargeMode);
        Assert.Empty(audit.Actions);
    }

    [Fact]
    public async Task AnOmittedDefaultModeSurvivesTurningOffAnotherMode()
    {
        var (handler, store, audit) = Handler();
        store.DefaultFreightChargeMode = FreightChargeMode.Free;

        await handler.HandleAsync(
            new SetFreightChargeModesCommand(TenantId, ["Free"]), TestContext.Current.CancellationToken);

        Assert.Equal(FreightChargeMode.Free, store.DefaultFreightChargeMode);
        Assert.Equal([SetFreightChargeModesHandler.AuditAction], audit.Actions);
    }

    // Apagar el que era el por defecto lo quita en el mismo guardado y en el mismo evento de auditoría.
    [Fact]
    public async Task TurningOffTheDefaultModeClearsIt()
    {
        var (handler, store, audit) = Handler();
        store.DefaultFreightChargeMode = FreightChargeMode.Free;

        var settings = await handler.HandleAsync(
            new SetFreightChargeModesCommand(TenantId, ["Charged"]), TestContext.Current.CancellationToken);

        Assert.Equal([FreightChargeMode.Charged], store.EnabledFreightChargeModes);
        Assert.Null(store.DefaultFreightChargeMode);
        Assert.Null(settings.DefaultFreightChargeMode);
        Assert.Equal([SetFreightChargeModesHandler.AuditAction], audit.Actions);
    }

    [Fact]
    public async Task TheSameModesAndDefaultAreNotAudited()
    {
        var (handler, store, audit) = Handler();
        store.DefaultFreightChargeMode = FreightChargeMode.Charged;

        await handler.HandleAsync(
            new SetFreightChargeModesCommand(
                TenantId, ["Free", "OnDelivery", "Charged"], SetsDefaultMode: true, DefaultMode: "Charged"),
            TestContext.Current.CancellationToken);

        Assert.Equal(FreightChargeMode.Charged, store.DefaultFreightChargeMode);
        Assert.Empty(audit.Actions);
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
