using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Reporte de uso de la plataforma: doble capa de operador, cortes por periodo en hora de Bogotá y armado del resultado.</summary>
public sealed class GetPlatformUsageHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly TenantId Other = TenantId.New();

    // Miércoles 2026-11-04 10:00 en Bogotá.
    private static readonly DateTimeOffset Now = At("2026-11-04T15:00:00Z");

    private static readonly DateTimeOffset Old = At("2026-01-01T00:00:00Z");

    private static GetPlatformUsageHandler Build(FixedPlatformUsageSource source, IExecutionContext context, DateTimeOffset? now = null) =>
        new(source, context, new FixedOperatorTenant(Operator.Value), new FixedClock(now ?? Now), new GetPlatformUsageValidator());

    private static DateTimeOffset At(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    private static OperatorContext Allowed() => new(Operator, OperatorPermissions.UsageRead);

    private static PlatformUsageTenantRow Row(
        string name, DateTimeOffset createdAt, int whatsApp = 0, int instagram = 0, Guid? id = null, params PlatformConnectedMemberDto[] connected) =>
        new(id ?? Guid.CreateVersion7(), name, createdAt, whatsApp, instagram, connected);

    [Theory]
    [InlineData("other-tenant")]
    [InlineData("no-permission")]
    [InlineData("not-operator")]
    public async Task AWrongTenantMissingPermissionOrNonOperatorIsForbidden(string scenario)
    {
        var source = new FixedPlatformUsageSource();
        var (context, routeTenant) = scenario switch
        {
            "other-tenant" => (new OperatorContext(Other, OperatorPermissions.UsageRead), Operator),
            "no-permission" => (new OperatorContext(Operator), Operator),
            _ => (new OperatorContext(Other, OperatorPermissions.UsageRead), Other),
        };

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            Build(source, context).HandleAsync(new GetPlatformUsageQuery(routeTenant, "day"), TestContext.Current.CancellationToken));
        Assert.Empty(source.Starts);
    }

    [Fact]
    public async Task AnUnknownPeriodIsAValidationError()
    {
        var source = new FixedPlatformUsageSource();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Build(source, Allowed()).HandleAsync(new GetPlatformUsageQuery(Operator, "year"), TestContext.Current.CancellationToken));

        Assert.Equal("Period", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(source.Starts);
    }

    [Theory]
    [InlineData("day", "2026-11-04T05:00:00Z", "day")]
    [InlineData("week", "2026-11-02T05:00:00Z", "week")]
    [InlineData("month", "2026-11-01T05:00:00Z", "month")]
    [InlineData(null, "2026-11-01T05:00:00Z", "month")]
    public async Task TheSourceReceivesTheStartOfTheChosenPeriod(string? period, string expectedStart, string expectedPeriod)
    {
        var source = new FixedPlatformUsageSource();

        var result = await Build(source, Allowed()).HandleAsync(new GetPlatformUsageQuery(Operator, period), TestContext.Current.CancellationToken);

        Assert.Equal(At(expectedStart), Assert.Single(source.Starts));
        Assert.Equal(expectedPeriod, result.Period);
    }

    [Fact]
    public async Task RegisteredCountsIgnoreThePeriod()
    {
        var source = new FixedPlatformUsageSource
        {
            Rows =
            [
                Row("hoy", At("2026-11-04T05:00:00Z")),
                Row("semana", At("2026-11-02T05:00:00Z")),
                Row("mes", At("2026-11-01T05:00:00Z")),
                Row("octubre", At("2026-11-01T04:59:00Z")),
                Row("viejo", Old),
            ],
        };

        foreach (var period in new[] { "day", "week", "month", null })
        {
            var result = await Build(source, Allowed()).HandleAsync(new GetPlatformUsageQuery(Operator, period), TestContext.Current.CancellationToken);

            Assert.Equal(new PlatformRegisteredCountsDto(1, 2, 3), result.Registered);
        }
    }

    [Fact]
    public async Task AWeekStartingInThePreviousMonthCountsMoreThanTheMonth()
    {
        // El 2026-11-01 es domingo, así que la semana inició el lunes 2026-10-26.
        var source = new FixedPlatformUsageSource
        {
            Rows =
            [
                Row("lunes", At("2026-10-26T05:00:00Z")),
                Row("sabado", At("2026-10-31T20:00:00Z")),
                Row("domingo", At("2026-11-01T10:00:00Z")),
                Row("antes", At("2026-10-26T04:59:00Z")),
            ],
        };

        var result = await Build(source, Allowed(), At("2026-11-01T15:00:00Z"))
            .HandleAsync(new GetPlatformUsageQuery(Operator, "week"), TestContext.Current.CancellationToken);

        Assert.Equal(At("2026-10-26T05:00:00Z"), Assert.Single(source.Starts));
        Assert.Equal(new PlatformRegisteredCountsDto(1, 3, 1), result.Registered);
    }

    [Fact]
    public async Task TheOperatorTenantIsLeftOut()
    {
        var source = new FixedPlatformUsageSource
        {
            Rows =
            [
                Row("QCode", Now, whatsApp: 9, id: Operator.Value),
                Row("Cliente", Now, whatsApp: 1),
            ],
        };

        var result = await Build(source, Allowed()).HandleAsync(new GetPlatformUsageQuery(Operator, "day"), TestContext.Current.CancellationToken);

        Assert.Equal("Cliente", Assert.Single(result.Tenants).Name);
        Assert.Equal(1, result.Registered.Day);
    }

    [Fact]
    public async Task TenantsAtZeroStay()
    {
        var source = new FixedPlatformUsageSource { Rows = [Row("Quieto", Old)] };

        var result = await Build(source, Allowed()).HandleAsync(new GetPlatformUsageQuery(Operator, "day"), TestContext.Current.CancellationToken);

        var tenant = Assert.Single(result.Tenants);
        Assert.Equal(new PlatformConversationCountsDto(0, 0, 0), tenant.Conversations);
        Assert.Empty(tenant.Connected);
    }

    [Fact]
    public async Task OrderIsTotalDescendingThenName()
    {
        var source = new FixedPlatformUsageSource
        {
            Rows =
            [
                Row("Beta", Old, whatsApp: 2),
                Row("Alfa", Old, whatsApp: 2),
                Row("Gama", Old, whatsApp: 5),
                Row("Cero", Old),
            ],
        };

        var result = await Build(source, Allowed()).HandleAsync(new GetPlatformUsageQuery(Operator, "day"), TestContext.Current.CancellationToken);

        Assert.Equal(["Gama", "Alfa", "Beta", "Cero"], result.Tenants.Select(tenant => tenant.Name));
    }

    [Fact]
    public async Task TotalSumsBothChannels()
    {
        var member = new PlatformConnectedMemberDto(Guid.CreateVersion7(), "Ana");
        var source = new FixedPlatformUsageSource { Rows = [Row("Uno", Old, whatsApp: 3, instagram: 4, connected: member)] };

        var result = await Build(source, Allowed()).HandleAsync(new GetPlatformUsageQuery(Operator, "day"), TestContext.Current.CancellationToken);

        var tenant = Assert.Single(result.Tenants);
        Assert.Equal(new PlatformConversationCountsDto(3, 4, 7), tenant.Conversations);
        Assert.Equal(member, Assert.Single(tenant.Connected));
    }
}

internal sealed class FixedPlatformUsageSource : IPlatformUsageSource
{
    public IReadOnlyList<PlatformUsageTenantRow> Rows { get; set; } = [];
    public List<DateTimeOffset> Starts { get; } = [];

    public Task<IReadOnlyList<PlatformUsageTenantRow>> ListAsync(DateTimeOffset startUtc, CancellationToken cancellationToken)
    {
        Starts.Add(startUtc);
        return Task.FromResult(Rows);
    }
}
